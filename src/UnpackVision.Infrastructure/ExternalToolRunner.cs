using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UnpackVision.Infrastructure;

public sealed record ExternalToolResult(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError);

public interface IExternalToolRunner
{
    Task<ExternalToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes only an explicit executable with an argument list. It never invokes a shell,
/// bounds captured output, and terminates the child process tree after a fixed timeout.
/// </summary>
public sealed class ExternalToolRunner : IExternalToolRunner
{
    public const int MaximumCapturedCharactersPerStream = 64 * 1024;
    private static readonly TimeSpan MaximumAllowedTimeout = TimeSpan.FromSeconds(30);

    public async Task<ExternalToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
        {
            return new ExternalToolResult(false, false, null, string.Empty, string.Empty);
        }

        var effectiveTimeout = timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : timeout > MaximumAllowedTimeout
                ? MaximumAllowedTimeout
                : timeout;

        using var process = new Process
        {
            StartInfo = ExternalToolProcessPolicy.CreateRestrictedRedirectedStartInfo(executablePath)
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new ExternalToolResult(false, false, null, string.Empty, string.Empty);
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return new ExternalToolResult(false, false, null, string.Empty, string.Empty);
        }

        var standardOutputTask = ReadAndDrainBoundedAsync(process.StandardOutput);
        var standardErrorTask = ReadAndDrainBoundedAsync(process.StandardError);
        using var timeoutCancellation = new CancellationTokenSource(effectiveTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        var timedOut = false;
        var exited = true;
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            exited = await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
        }
        catch (OperationCanceledException)
        {
            await ExternalToolProcessPolicy.TerminateProcessTreeAsync(process);
            throw;
        }

        string standardOutput;
        string standardError;
        if (exited)
        {
            standardOutput = await standardOutputTask;
            standardError = await standardErrorTask;
        }
        else
        {
            // A wedged hardware encoder can ignore process-tree termination. Do not let
            // a capability probe block application startup indefinitely while waiting for
            // redirected pipe EOF; disposing the readers releases our handles instead.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            ExternalToolProcessPolicy.ObserveFault(standardOutputTask);
            ExternalToolProcessPolicy.ObserveFault(standardErrorTask);
            standardOutput = string.Empty;
            standardError = string.Empty;
        }
        return new ExternalToolResult(
            true,
            timedOut,
            timedOut || !exited ? null : process.ExitCode,
            standardOutput,
            standardError);
    }

    private static async Task<string> ReadAndDrainBoundedAsync(StreamReader reader)
    {
        var output = new StringBuilder(MaximumCapturedCharactersPerStream);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            var remaining = MaximumCapturedCharactersPerStream - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
        return output.ToString();
    }

}
