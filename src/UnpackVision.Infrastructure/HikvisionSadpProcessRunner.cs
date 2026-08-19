using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

internal enum HikvisionSadpOutputStatus
{
    Valid,
    Invalid,
    TooLarge
}

internal sealed record HikvisionSadpProcessResult(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    HikvisionSadpOutputStatus OutputStatus,
    IReadOnlyList<IpcDiscoveryDevice> Devices);

internal interface IHikvisionSadpProcessRunner
{
    Task<HikvisionSadpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumDevices,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the pinned SADP discovery command and projects its stdout directly through the
/// strict XML parser. Unlike the general-purpose tool runner, this adapter never creates
/// a complete managed stdout string containing device serial numbers or MAC addresses.
/// </summary>
internal sealed class StreamingHikvisionSadpProcessRunner : IHikvisionSadpProcessRunner
{
    private static readonly TimeSpan MaximumAllowedTimeout = TimeSpan.FromSeconds(30);

    public async Task<HikvisionSadpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumDevices,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            return NotStarted();
        }

        var effectiveTimeout = timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : timeout > MaximumAllowedTimeout
                ? MaximumAllowedTimeout
                : timeout;

        using var process = new Process
        {
            StartInfo = ExternalToolProcessPolicy.CreateRestrictedRedirectedStartInfo(
                executablePath,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: false))
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return NotStarted();
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return NotStarted();
        }

        // Both pipes are consumed immediately so a verbose child cannot deadlock on a full
        // redirected buffer. Stderr is intentionally discarded rather than returned or logged.
        var parseTask = Task.Run(
            () => HikvisionSadpXmlParser.Parse(process.StandardOutput, maximumDevices),
            CancellationToken.None);
        var standardErrorTask = DrainAndDiscardAsync(process.StandardError);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(effectiveTimeout, CancellationToken.None);
        var cancellationSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationSignal.TrySetResult(true));
        var cancellationTask = cancellationSignal.Task;

        var completed = await Task.WhenAny(exitTask, parseTask, timeoutTask, cancellationTask);
        if (completed == cancellationTask)
        {
            var exitedAfterCancellation = await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
            CompleteOrDetachPipeTasks(process, parseTask, standardErrorTask, exitedAfterCancellation);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (completed == parseTask)
        {
            var earlyParse = await parseTask;
            if (earlyParse.Status == HikvisionSadpOutputStatus.TooLarge)
            {
                var exitedAfterLimit = await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
                CompleteOrDetachPipeTasks(process, parseTask, standardErrorTask, exitedAfterLimit);
                return new HikvisionSadpProcessResult(
                    true,
                    false,
                    exitedAfterLimit ? process.ExitCode : null,
                    HikvisionSadpOutputStatus.TooLarge,
                    []);
            }

            completed = await Task.WhenAny(exitTask, timeoutTask, cancellationTask);
            if (completed == cancellationTask)
            {
                var exitedAfterCancellation = await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
                CompleteOrDetachPipeTasks(process, parseTask, standardErrorTask, exitedAfterCancellation);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (completed == timeoutTask)
        {
            var exitedAfterTimeout = await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
            CompleteOrDetachPipeTasks(process, parseTask, standardErrorTask, exitedAfterTimeout);
            return new HikvisionSadpProcessResult(
                true,
                true,
                null,
                HikvisionSadpOutputStatus.Invalid,
                []);
        }

        var parsed = await parseTask;
        await standardErrorTask;
        return new HikvisionSadpProcessResult(
            true,
            false,
            process.ExitCode,
            parsed.Status,
            parsed.Devices);
    }

    private static HikvisionSadpProcessResult NotStarted() => new(
        false,
        false,
        null,
        HikvisionSadpOutputStatus.Invalid,
        []);

    private static async Task DrainAndDiscardAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0)
        {
            // The exit code is sufficient for diagnostics. Vendor output can contain device
            // identifiers, so stderr is deliberately neither retained nor surfaced.
        }
    }

    private static void CompleteOrDetachPipeTasks(
        Process process,
        Task parseTask,
        Task standardErrorTask,
        bool processExited)
    {
        if (!processExited)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
        }
        ExternalToolProcessPolicy.ObserveFault(parseTask);
        ExternalToolProcessPolicy.ObserveFault(standardErrorTask);
    }
}
