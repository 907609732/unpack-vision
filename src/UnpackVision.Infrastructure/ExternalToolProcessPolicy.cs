using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Shared process boundary for bundled media and discovery helpers. Child tools receive a
/// deterministic environment and their complete descendant tree is terminated on timeout
/// or caller cancellation.
/// </summary>
internal static class ExternalToolProcessPolicy
{
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(2);

    internal static ProcessStartInfo CreateRestrictedRedirectedStartInfo(
        string executablePath,
        Encoding? standardOutputEncoding = null,
        Encoding? standardErrorEncoding = null)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = executableDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (standardOutputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = standardOutputEncoding;
        }
        if (standardErrorEncoding is not null)
        {
            startInfo.StandardErrorEncoding = standardErrorEncoding;
        }

        // Bundled helpers never need desktop credentials, proxy variables, SDK overrides,
        // application secrets, or the current working directory's executable search path.
        startInfo.Environment.Clear();
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        AddEnvironmentValue(startInfo, "SystemRoot", windowsDirectory);
        AddEnvironmentValue(startInfo, "WINDIR", windowsDirectory);
        AddEnvironmentValue(
            startInfo,
            "SystemDrive",
            Path.GetPathRoot(windowsDirectory)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        AddEnvironmentValue(startInfo, "TEMP", temporaryDirectory);
        AddEnvironmentValue(startInfo, "TMP", temporaryDirectory);

        var executableSearchPath = new[] { executableDirectory, systemDirectory, windowsDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, executableSearchPath);
        return startInfo;
    }

    internal static async Task<bool> TerminateProcessTreeAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            TryKillProcessTree(process);
            try
            {
                if (process.HasExited)
                {
                    return true;
                }

                using var grace = new CancellationTokenSource(TerminationGracePeriod);
                await process.WaitForExitAsync(grace.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Retry once because a descendant can be created while Windows is taking
                // the first process-tree snapshot.
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        return false;
    }

    internal static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void AddEnvironmentValue(
        ProcessStartInfo startInfo,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                Win32Exception or
                AggregateException or
                NotSupportedException)
        {
            // Cleanup is best-effort; the bounded retry loop prevents a denied descendant
            // from blocking application startup indefinitely.
        }
    }
}
