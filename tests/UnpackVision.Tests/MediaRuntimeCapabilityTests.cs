using UnpackVision.Core;
using UnpackVision.Infrastructure;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace UnpackVision.Tests;

public sealed class MediaRuntimeCapabilityTests
{
    [Fact]
    public async Task ExternalToolRunnerBoundsAProcessThatDoesNotExitByItself()
    {
        var powershell = GetWindowsPowerShellPath();
        Assert.True(File.Exists(powershell));
        var stopwatch = Stopwatch.StartNew();

        var result = await new ExternalToolRunner().RunAsync(
            powershell,
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            TimeSpan.FromMilliseconds(250));

        stopwatch.Stop();
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public Task ExternalToolRunnerTerminatesDescendantsAfterTimeout() =>
        AssertProcessTreeIsTerminatedAsync(cancelCaller: false);

    [Fact]
    public Task ExternalToolRunnerTerminatesDescendantsAfterCancellation() =>
        AssertProcessTreeIsTerminatedAsync(cancelCaller: true);

    [Fact]
    public async Task ExternalToolRunnerDoesNotInheritUnlistedDesktopEnvironmentVariables()
    {
        var powershell = GetWindowsPowerShellPath();
        Assert.True(File.Exists(powershell));
        var sentinelName = $"UNPACKVISION_TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(sentinelName, "must-not-leak");

        try
        {
            var script = $"if ([Environment]::GetEnvironmentVariable('{sentinelName}', 'Process')) " +
                         "{ 'LEAKED' } else { 'FILTERED' }; " +
                         "if ([string]::IsNullOrWhiteSpace($env:SystemRoot)) { 'NO_SYSTEM_ROOT' } else { 'SYSTEM_ROOT_OK' }";

            var result = await new ExternalToolRunner().RunAsync(
                powershell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(script)],
                TimeSpan.FromSeconds(10));

            Assert.True(result.Started);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("FILTERED", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("SYSTEM_ROOT_OK", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-leak", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, null);
        }
    }

    [Fact]
    public async Task GStreamerProbeReportsRequiredElementsAndTestedEncoders()
    {
        using var runtime = TestRuntime.CreateGStreamer();
        var runner = new FakeExternalToolRunner(call =>
        {
            if (string.Equals(call.ExecutablePath, runtime.LaunchPath, StringComparison.OrdinalIgnoreCase))
            {
                return Success();
            }
            if (call.Arguments.SequenceEqual(["--version"]))
            {
                return Success("gst-inspect-1.0 version 1.28.5\nGStreamer 1.28.5");
            }
            var element = Assert.Single(call.Arguments.Skip(1));
            return new HashSet<string>(StringComparer.Ordinal)
            {
                "mfvideosrc", "rtspsrc", "textoverlay", "mp4mux", "appsink",
                "fdsrc", "videoparse", "queue", "videoconvert", "h264parse", "filesink",
                "qsvh264enc", "mfh264enc", "openh264enc", "x264enc"
            }.Contains(element)
                ? Success()
                : Failure();
        });
        var locator = new GStreamerRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(runtime.InspectPath, MediaRuntimeOrigin.Bundled)
        ]);

        var result = await new GStreamerRuntimeProbe(locator, runner).ProbeAsync();

        Assert.True(result.RuntimeFound);
        Assert.True(result.IsRequiredVersion);
        Assert.True(result.HasAllRequiredElements);
        Assert.Equal("1.28.5", result.Version);
        Assert.Equal(runtime.LaunchPath, result.LaunchExecutablePath);
        Assert.All(result.RequiredElements, element => Assert.True(element.Value));
        Assert.True(Assert.Single(result.Encoders, encoder => encoder.ElementName == "qsvh264enc").Available);
        var x264 = Assert.Single(result.Encoders, encoder => encoder.ElementName == "x264enc");
        Assert.False(x264.Available);
        Assert.False(x264.ApprovedForBundledRedistribution);
        Assert.Contains("higher-priority", x264.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(runner.Calls, call =>
        {
            Assert.True(
                string.Equals(call.ExecutablePath, runtime.InspectPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(call.ExecutablePath, runtime.LaunchPath, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(call.Arguments, argument => argument.Contains("rtsp://", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(call.Arguments, argument => argument.Contains("password", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(runner.Calls, call =>
            string.Equals(call.ExecutablePath, runtime.LaunchPath, StringComparison.OrdinalIgnoreCase) &&
            call.Arguments.Contains("qsvh264enc"));
    }

    [Fact]
    public async Task GStreamerProbeRejectsWrongVersionWithoutInspectingPlugins()
    {
        using var runtime = TestRuntime.CreateGStreamer();
        var runner = new FakeExternalToolRunner(_ => Success("GStreamer 1.26.2"));
        var locator = new GStreamerRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(runtime.InspectPath, MediaRuntimeOrigin.System)
        ]);

        var result = await new GStreamerRuntimeProbe(locator, runner).ProbeAsync();

        Assert.True(result.RuntimeFound);
        Assert.False(result.IsRequiredVersion);
        Assert.Equal("1.26.2", result.Version);
        Assert.Single(runner.Calls);
        Assert.Equal(["--version"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task MissingRuntimesReturnUnavailableInsteadOfThrowing()
    {
        var missingGstreamer = new GStreamerRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gst-inspect-1.0.exe"),
                MediaRuntimeOrigin.System)
        ]);
        var missingFfmpeg = new FfmpegRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ffmpeg.exe"),
                MediaRuntimeOrigin.System)
        ]);

        var gstreamer = await new GStreamerRuntimeProbe(missingGstreamer, new FakeExternalToolRunner(_ => Success())).ProbeAsync();
        var ffmpeg = await new FfmpegRuntimeProbe(missingFfmpeg, new FakeExternalToolRunner(_ => Success())).ProbeAsync();

        Assert.False(gstreamer.RuntimeFound);
        Assert.False(ffmpeg.RuntimeFound);
    }

    [Fact]
    public void EncoderSelectorUsesInspectedCapabilityOrderAndRejectsUnapprovedFallback()
    {
        var required = RequiredElements(available: true);
        var capabilities = new GStreamerRuntimeCapabilities(
            true,
            true,
            GStreamerRuntimeProbe.RequiredVersion,
            GStreamerRuntimeProbe.RequiredVersion,
            MediaRuntimeOrigin.Bundled,
            "gst-inspect-1.0.exe",
            "gst-launch-1.0.exe",
            required,
            [
                new(MediaEncoderFamily.Software, "openh264enc", true, false, true),
                new(MediaEncoderFamily.NvidiaNvenc, "nvh264enc", true, true, true),
                new(MediaEncoderFamily.IntelQuickSync, "qsvh264enc", true, true, true)
            ]);
        var selector = new GStreamerH264EncoderSelector();

        var selected = selector.Select(capabilities, requireHardwareAcceleration: true);

        Assert.True(selected.Success);
        Assert.Equal("qsvh264enc", selected.Encoder?.ElementName);

        var unapprovedOnly = capabilities with
        {
            Encoders = [new(MediaEncoderFamily.Software, "x264enc", true, false, false)]
        };
        var rejected = selector.Select(unapprovedOnly, requireHardwareAcceleration: false);
        Assert.False(rejected.Success);
        Assert.Null(rejected.Encoder);
    }

    [Fact]
    public async Task FfmpegProbeRejectsGplAndNonfreeBuildMetadata()
    {
        using var runtime = TestRuntime.CreateFfmpeg();
        var runner = new FakeExternalToolRunner(_ => Success("""
            ffmpeg version 8.1.2-full_build
            configuration: --enable-gpl --enable-nonfree --enable-libx264
            """));
        var locator = new FfmpegRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(runtime.FfmpegPath, MediaRuntimeOrigin.Path)
        ]);

        var result = await new FfmpegRuntimeProbe(locator, runner).ProbeAsync();

        Assert.True(result.RuntimeFound);
        Assert.True(result.IsExpectedVersion);
        Assert.True(result.GplEnabled);
        Assert.True(result.NonFreeEnabled);
        Assert.False(result.ApprovedForBundledRedistribution);
        Assert.Equal(["-hide_banner", "-version"], Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task FfmpegProbeApprovesExpectedLgplBuildMetadata()
    {
        using var runtime = TestRuntime.CreateFfmpeg();
        var runner = new FakeExternalToolRunner(_ => Success("""
            ffmpeg version 8.1.2
            configuration: --disable-gpl --disable-nonfree --enable-shared
            """));
        var locator = new FfmpegRuntimeLocator(explicitCandidates:
        [
            new MediaRuntimeExecutableCandidate(runtime.FfmpegPath, MediaRuntimeOrigin.Bundled)
        ]);

        var result = await new FfmpegRuntimeProbe(locator, runner).ProbeAsync();

        Assert.True(result.ApprovedForBundledRedistribution);
        Assert.False(result.GplEnabled);
        Assert.False(result.NonFreeEnabled);
    }

    private static IReadOnlyDictionary<string, bool> RequiredElements(bool available) =>
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["mfvideosrc"] = available,
            ["rtspsrc"] = available,
            ["textoverlay"] = available,
            ["mp4mux"] = available,
            ["appsink"] = available,
            ["fdsrc"] = available,
            ["videoparse"] = available,
            ["queue"] = available,
            ["videoconvert"] = available,
            ["h264parse"] = available,
            ["filesink"] = available
        };

    private static async Task AssertProcessTreeIsTerminatedAsync(bool cancelCaller)
    {
        var powershell = GetWindowsPowerShellPath();
        Assert.True(File.Exists(powershell));
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "UnpackVision.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var childPidPath = Path.Combine(temporaryDirectory, "child.pid");
        int? childPid = null;

        try
        {
            var childCommand = "Start-Sleep -Seconds 30";
            var escapedPowerShell = powershell.Replace("'", "''", StringComparison.Ordinal);
            var escapedPidPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
            var parentCommand =
                $"$child = Start-Process -FilePath '{escapedPowerShell}' " +
                $"-ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-EncodedCommand','{EncodePowerShell(childCommand)}') " +
                "-WindowStyle Hidden -PassThru; " +
                $"Set-Content -LiteralPath '{escapedPidPath}' -Value $child.Id -Encoding Ascii; " +
                "Start-Sleep -Seconds 30";
            using var cancellation = new CancellationTokenSource();
            var deadline = ProcessTreeTestDeadline();
            if (cancelCaller)
            {
                cancellation.CancelAfter(deadline);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new ExternalToolRunner().RunAsync(
                        powershell,
                        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(parentCommand)],
                        TimeSpan.FromSeconds(20),
                        cancellation.Token));
            }
            else
            {
                var result = await new ExternalToolRunner().RunAsync(
                    powershell,
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(parentCommand)],
                    deadline);
                Assert.True(result.Started);
                Assert.True(result.TimedOut);
                Assert.Null(result.ExitCode);
            }

            Assert.True(File.Exists(childPidPath), "The parent probe did not start its child before termination.");
            childPid = int.Parse((await File.ReadAllTextAsync(childPidPath)).Trim(), CultureInfo.InvariantCulture);
            await AssertProcessExitedAsync(childPid.Value);
        }
        finally
        {
            if (childPid is not null)
            {
                TryKillProcess(childPid.Value);
            }
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Delayed antivirus handles must not hide the process-tree assertion.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }

    private static TimeSpan ProcessTreeTestDeadline() =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(8);

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Child process {processId} remained alive after its probe was terminated.");
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
        catch (InvalidOperationException)
        {
            // The process exited while cleanup was checking it.
        }
    }

    private static string GetWindowsPowerShellPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    private static string EncodePowerShell(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static ExternalToolResult Success(string standardOutput = "") =>
        new(true, false, 0, standardOutput, string.Empty);

    private static ExternalToolResult Failure() =>
        new(true, false, 1, string.Empty, string.Empty);

    private sealed class FakeExternalToolRunner(
        Func<ExternalToolCall, ExternalToolResult> execute) : IExternalToolRunner
    {
        public List<ExternalToolCall> Calls { get; } = [];

        public Task<ExternalToolResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new ExternalToolCall(executablePath, arguments.ToArray(), timeout);
            Calls.Add(call);
            return Task.FromResult(execute(call));
        }
    }

    private sealed record ExternalToolCall(
        string ExecutablePath,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout);

    private sealed class TestRuntime : IDisposable
    {
        private TestRuntime(string directory)
        {
            Directory = directory;
            InspectPath = Path.Combine(directory, GStreamerRuntimeLocator.InspectExecutableName);
            LaunchPath = Path.Combine(directory, GStreamerRuntimeLocator.LaunchExecutableName);
            FfmpegPath = Path.Combine(directory, FfmpegRuntimeLocator.ExecutableName);
        }

        public string Directory { get; }
        public string InspectPath { get; }
        public string LaunchPath { get; }
        public string FfmpegPath { get; }

        public static TestRuntime CreateGStreamer()
        {
            var runtime = Create();
            File.WriteAllBytes(runtime.InspectPath, []);
            File.WriteAllBytes(runtime.LaunchPath, []);
            return runtime;
        }

        public static TestRuntime CreateFfmpeg()
        {
            var runtime = Create();
            File.WriteAllBytes(runtime.FfmpegPath, []);
            return runtime;
        }

        private static TestRuntime Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UnpackVision.Tests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return new TestRuntime(directory);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup must not hide the assertion result on a delayed antivirus handle.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }
}
