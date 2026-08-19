using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class HikvisionSadpDiscoveryTests
{
    [Fact]
    public void XmlParserFindsStrictDocumentAndProjectsOnlySafeFields()
    {
        var output = """
            Discovering Hikvision devices via SADP protocol...
            Sending multicast probes to 239.255.255.250:37020
            <?xml version="1.0" encoding="UTF-8"?>
            <SADPDeviceList version="2.0">
              <ProbeMatch>
                <DeviceType>DS-2CD Test</DeviceType>
                <DeviceSN>SERIAL-DO-NOT-LEAK</DeviceSN>
                <MAC>00:11:22:33:44:55</MAC>
                <IPv4Address>192.168.31.64</IPv4Address>
                <CommandPort>8000</CommandPort>
                <HttpPort>8080</HttpPort>
                <AnalogChannelNum>1</AnalogChannelNum>
                <DigitalChannelNum>0</DigitalChannelNum>
              </ProbeMatch>
              <ProbeMatch>
                <DeviceType>DS-7608 Test</DeviceType>
                <DeviceSN>ANOTHER-SECRET-SERIAL</DeviceSN>
                <MAC>AA:BB:CC:DD:EE:FF</MAC>
                <IPv4Address>192.168.31.65</IPv4Address>
                <CommandPort>8000</CommandPort>
                <HttpPort>80</HttpPort>
                <AnalogChannelNum>4</AnalogChannelNum>
                <DigitalChannelNum>4</DigitalChannelNum>
              </ProbeMatch>
            </SADPDeviceList>
            """;

        var parsed = HikvisionSadpXmlParser.Parse(new StringReader(output), 64);
        var devices = parsed.Devices;

        Assert.Equal(HikvisionSadpOutputStatus.Valid, parsed.Status);
        Assert.Equal(2, devices.Count);
        Assert.All(devices, device => Assert.Equal(IpcDiscoverySource.HikvisionSadp, device.Source));
        Assert.Contains(devices, device => device.RemoteAddress == "192.168.31.64" &&
                                           device.Addresses.Single().StartsWith("http://192.168.31.64:8080/", StringComparison.Ordinal));
        Assert.Contains(devices, device => device.RemoteAddress == "192.168.31.65" &&
                                           device.DisplayName.Contains("8 路", StringComparison.Ordinal));

        var projectedJson = JsonSerializer.Serialize(devices);
        Assert.DoesNotContain("SERIAL-DO-NOT-LEAK", projectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ANOTHER-SECRET-SERIAL", projectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("00:11:22:33:44:55", projectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", projectedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlParserRejectsUnexpectedRootDtdPublicAddressesAndOversizedOutput()
    {
        Assert.Equal(
            HikvisionSadpOutputStatus.Invalid,
            HikvisionSadpXmlParser.Parse(new StringReader("<Unexpected />"), 64).Status);

        Assert.Equal(
            HikvisionSadpOutputStatus.Invalid,
            HikvisionSadpXmlParser.Parse(new StringReader(
                "<!DOCTYPE SADPDeviceList [<!ENTITY xxe SYSTEM 'file:///C:/Windows/win.ini'>]><SADPDeviceList />"), 64).Status);

        var publicAddress = SadpXml("8.8.8.8", "Camera", "S", "M", 80, 1);
        var filtered = HikvisionSadpXmlParser.Parse(new StringReader(publicAddress), 64);
        Assert.Equal(HikvisionSadpOutputStatus.Valid, filtered.Status);
        Assert.Empty(filtered.Devices);

        Assert.Equal(
            HikvisionSadpOutputStatus.TooLarge,
            HikvisionSadpXmlParser.Parse(
                new StringReader(new string('x', HikvisionSadpDiscoveryService.MaximumDiscoveryOutputCharacters + 1)),
                64).Status);
    }

    [Fact]
    public void XmlParserStreamsFragmentedOutputWithoutReadToEndOrReadLine()
    {
        var output = SadpXml(
            "192.168.31.64",
            "DS-2CD Test",
            new string('S', 4096),
            new string('M', 4096),
            80,
            1);
        using var source = new FragmentedTextReader(output, maximumChunkSize: 7);

        var parsed = HikvisionSadpXmlParser.Parse(source, 64);

        Assert.Equal(HikvisionSadpOutputStatus.Valid, parsed.Status);
        Assert.Single(parsed.Devices);
        Assert.False(source.ReadToEndCalled);
        Assert.False(source.ReadLineCalled);
        Assert.True(source.SingleCharacterReads > 0);
    }

    [Fact]
    public async Task AdapterInvokesOnlyBoundedReadOnlyDiscoveryCommand()
    {
        ExternalToolCall? call = null;
        var output = SadpXml("192.168.31.64", "Camera", "SECRET-SERIAL", "SECRET-MAC", 80, 1);
        var runner = new FakeSadpProcessRunner(next =>
        {
            call = next;
            var parsed = HikvisionSadpXmlParser.Parse(new StringReader(output), next.MaximumDevices);
            return new HikvisionSadpProcessResult(true, false, 0, parsed.Status, parsed.Devices);
        });
        var service = new HikvisionSadpDiscoveryService(
            @"C:\verified\sadp.exe",
            runner,
            new FixedConflictProbe(false, false),
            new AvailableToolVerifier());

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)));

        Assert.NotNull(call);
        Assert.Equal(@"C:\verified\sadp.exe", call!.ExecutablePath);
        Assert.Equal(["discover:sadp", "-xml", "-timeout", "5s"], call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(8), call.Timeout);
        Assert.Equal(64, call.MaximumDevices);
        Assert.DoesNotContain(call.Arguments, argument =>
            argument.Contains("send", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("output", StringComparison.OrdinalIgnoreCase));
        var device = Assert.Single(result.Devices);
        Assert.Equal("192.168.31.64", device.RemoteAddress);
        Assert.Empty(result.Notices);
        var projectedJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SECRET-SERIAL", projectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-MAC", projectedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdapterReportsConflictAndDoesNotPresentEmptyResultAsNoDevices()
    {
        var runner = new FakeSadpProcessRunner(_ =>
            new HikvisionSadpProcessResult(
                true,
                false,
                0,
                HikvisionSadpOutputStatus.Valid,
                []));
        var service = new HikvisionSadpDiscoveryService(
            @"C:\verified\sadp.exe",
            runner,
            new FixedConflictProbe(true, true),
            new AvailableToolVerifier());

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)));

        Assert.Empty(result.Devices);
        Assert.Contains(result.Notices, notice => notice.Contains("37020", StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice => notice.Contains("0 台不代表", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdapterReportsStreamingOutputLimitWithoutReturningPartialDevices()
    {
        var runner = new FakeSadpProcessRunner(_ =>
            new HikvisionSadpProcessResult(
                true,
                false,
                0,
                HikvisionSadpOutputStatus.TooLarge,
                []));
        var service = new HikvisionSadpDiscoveryService(
            @"C:\verified\sadp.exe",
            runner,
            new FixedConflictProbe(false, false),
            new AvailableToolVerifier());

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)));

        Assert.Empty(result.Devices);
        Assert.Contains(result.Notices, notice => notice.Contains("安全上限", StringComparison.Ordinal));
    }

    [Fact]
    public void DedicatedSadpRunnerBoundaryCannotCarryRawStdoutOrStderr()
    {
        Assert.DoesNotContain(
            typeof(HikvisionSadpProcessResult).GetProperties(),
            property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(
            typeof(HikvisionSadpDiscoveryService).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(IExternalToolRunner));
    }

    [Fact]
    public async Task DedicatedRunnerStreamsProcessOutputIntoSafeProjection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var commandPrompt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var fixturePath = Path.Combine(Path.GetTempPath(), $"sadp-stream-{Guid.NewGuid():N}.xml");
        const string serial = "PROCESS-SECRET-SERIAL";
        const string mac = "PROCESS-SECRET-MAC";
        try
        {
            await File.WriteAllTextAsync(
                fixturePath,
                SadpXml("192.168.31.64", "Camera", serial, mac, 80, 1),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await new StreamingHikvisionSadpProcessRunner().RunAsync(
                commandPrompt,
                ["/d", "/c", "type", fixturePath],
                TimeSpan.FromSeconds(3),
                maximumDevices: 64);

            Assert.True(result.Started);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(HikvisionSadpOutputStatus.Valid, result.OutputStatus);
            Assert.Single(result.Devices);
            var projectedJson = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(serial, projectedJson, StringComparison.Ordinal);
            Assert.DoesNotContain(mac, projectedJson, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(fixturePath))
            {
                File.Delete(fixturePath);
            }
        }
    }

    [Fact]
    public async Task DedicatedRunnerDoesNotInheritUnlistedDesktopEnvironmentVariables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sentinelName = $"UNPACKVISION_SADP_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(sentinelName, "must-not-leak");
        try
        {
            var script =
                $"$name = '{sentinelName}'; " +
                "$visible = if ([Environment]::GetEnvironmentVariable($name, 'Process')) { 'LEAKED' } else { 'FILTERED' }; " +
                "$xml = '<SADPDeviceList><ProbeMatch><DeviceType>' + $visible + " +
                "'</DeviceType><IPv4Address>192.168.31.64</IPv4Address><HttpPort>80</HttpPort>' + " +
                "'<AnalogChannelNum>1</AnalogChannelNum><DigitalChannelNum>0</DigitalChannelNum>' + " +
                "'</ProbeMatch></SADPDeviceList>'; " +
                "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::Write($xml)";

            var result = await new StreamingHikvisionSadpProcessRunner().RunAsync(
                GetWindowsPowerShellPath(),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(script)],
                TimeSpan.FromSeconds(10),
                maximumDevices: 64);

            Assert.True(result.Started);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(HikvisionSadpOutputStatus.Valid, result.OutputStatus);
            Assert.Equal("FILTERED", Assert.Single(result.Devices).DisplayName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DedicatedRunnerTerminatesDescendants(bool cancelCaller) =>
        AssertSadpProcessTreeIsTerminatedAsync(cancelCaller);

    [Fact]
    public async Task HashPinnedVerifierRejectsAnyUnapprovedExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sadp-tampered-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllTextAsync(path, "not the pinned executable");

            var notice = await new HashPinnedHikvisionSadpToolVerifier()
                .VerifyAsync(path, CancellationToken.None);

            Assert.NotNull(notice);
            Assert.Contains("完整性校验失败", notice, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PublishStageAndRuntimeLocatorUseTheSamePinnedToolContract()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var stage = File.ReadAllText(Path.Combine(testData, "stage-hikvision-sadp.ps1"));
        var verify = File.ReadAllText(Path.Combine(testData, "verify-hikvision-sadp-package.ps1"));
        var publish = File.ReadAllText(Path.Combine(testData, "publish.ps1"));
        var package = File.ReadAllText(Path.Combine(testData, "package-release.ps1"));
        var notices = File.ReadAllText(Path.Combine(testData, "THIRD_PARTY_NOTICES.md"));

        Assert.Contains("v1.0.43-afa00e3", stage, StringComparison.Ordinal);
        Assert.Contains("edaf3e99ca155e640c94608bbb132ef386e11b0716fc22fb959666733424fda5", stage, StringComparison.Ordinal);
        Assert.Contains(HikvisionSadpDiscoveryService.ExpectedExecutableSha256, stage, StringComparison.Ordinal);
        Assert.Contains("e0fefd96c0c47be566ec00efe755dba1966709f798036ccb96b80c7127cf9fe6", stage, StringComparison.Ordinal);
        Assert.Contains("tools or artifacts", stage, StringComparison.Ordinal);
        Assert.Contains("stage-hikvision-sadp.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("verify-hikvision-sadp-package.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("verify-hikvision-sadp-package.ps1", package, StringComparison.Ordinal);
        Assert.Contains("runtimes\\hikvision-tooling\\1.0.43", publish, StringComparison.Ordinal);
        Assert.Contains("runtimes\\hikvision-tooling\\1.0.43", package, StringComparison.Ordinal);
        Assert.Contains("Publish output must stay below", publish, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Copy-Item -LiteralPath $sadpStage -Destination $sadpOutput -Recurse",
            publish,
            StringComparison.Ordinal);
        Assert.Contains(HikvisionSadpDiscoveryService.ExpectedExecutableSha256, verify, StringComparison.Ordinal);
        Assert.Contains("e0fefd96c0c47be566ec00efe755dba1966709f798036ccb96b80c7127cf9fe6", verify, StringComparison.Ordinal);
        Assert.Contains("must contain exactly the audited executable", verify, StringComparison.Ordinal);
        Assert.Contains("LICENSE-go.txt", verify, StringComparison.Ordinal);
        Assert.Contains("NOTICE-hikvision-tooling.txt", verify, StringComparison.Ordinal);
        Assert.Contains("unexpected files or directories", stage, StringComparison.Ordinal);
        Assert.Contains("hikvision-tooling", notices, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("runtimes", "hikvision-tooling", "1.0.43", "sadp.exe"),
            HikvisionSadpToolLocator.GetPackagedExecutablePath(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompositeMergesSameIpPreservesOnvifNameAndAddsSadpMetadata()
    {
        var onvif = new IpcDiscoveryDevice(
            "urn:uuid:camera",
            "收货台",
            string.Empty,
            string.Empty,
            "192.168.31.64",
            ["http://192.168.31.64/onvif/device_service"],
            ["onvif://www.onvif.org/name/Receiving"]);
        var sadp = new IpcDiscoveryDevice(
            string.Empty,
            "DS-2CD Test",
            "Hikvision",
            "DS-2CD Test",
            "192.168.31.64",
            ["http://192.168.31.64/"],
            [])
        {
            Source = IpcDiscoverySource.HikvisionSadp
        };
        var service = new CompositeIpcDiscoveryService(
            new FixedDiscoveryService(new IpcDiscoveryScanResult([onvif], ["ONVIF notice"])),
            new FixedDiscoveryService(new IpcDiscoveryScanResult([sadp], ["SADP notice"])));

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)));

        var merged = Assert.Single(result.Devices);
        Assert.Equal("收货台", merged.DisplayName);
        Assert.Equal("Hikvision", merged.Manufacturer);
        Assert.Equal("DS-2CD Test", merged.Model);
        Assert.Equal(IpcDiscoverySource.HikvisionSadp, merged.Source);
        Assert.Equal(2, merged.Addresses.Count);
        Assert.Equal(["ONVIF notice", "SADP notice"], result.Notices);
    }

    [Fact]
    public void CompositeTreatsCompatibilityProbeAsOnvifFamilyWhenSadpArrivesFirst()
    {
        var sadp = new IpcDiscoveryDevice(
            string.Empty,
            "DS-2CD Test",
            "Hikvision",
            "DS-2CD Test",
            "192.168.31.64",
            ["http://192.168.31.64/"],
            [])
        {
            Source = IpcDiscoverySource.HikvisionSadp
        };
        var compatibility = new IpcDiscoveryDevice(
            "urn:uuid:compatibility-camera",
            "收货台",
            string.Empty,
            string.Empty,
            "192.168.31.64",
            ["http://192.168.31.64/onvif/device_service"],
            ["onvif://www.onvif.org/name/Receiving"])
        {
            Source = IpcDiscoverySource.CompatibilityProbe
        };

        var merged = Assert.Single(CompositeIpcDiscoveryService.MergeDevices(
            [sadp, compatibility],
            maximumDevices: 64));

        Assert.Equal("urn:uuid:compatibility-camera", merged.EndpointReference);
        Assert.Equal("收货台", merged.DisplayName);
        Assert.Equal("Hikvision", merged.Manufacturer);
        Assert.Equal("DS-2CD Test", merged.Model);
        Assert.Equal(IpcDiscoverySource.HikvisionSadp, merged.Source);
        Assert.Contains("http://192.168.31.64/onvif/device_service", merged.Addresses);
        Assert.Contains("onvif://www.onvif.org/name/Receiving", merged.Scopes);
    }

    [Fact]
    public async Task CompositeKeepsSuccessfulSourceWhenOtherSourceFails()
    {
        var sadp = new IpcDiscoveryDevice(
            string.Empty,
            "Camera",
            "Hikvision",
            "Camera",
            "192.168.31.64",
            ["http://192.168.31.64/"],
            [])
        {
            Source = IpcDiscoverySource.HikvisionSadp
        };
        var service = new CompositeIpcDiscoveryService(
            new ThrowingDiscoveryService(),
            new FixedDiscoveryService(new IpcDiscoveryScanResult([sadp], [])));

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)));

        Assert.Single(result.Devices);
        Assert.Contains(result.Notices, notice => notice.Contains("ONVIF", StringComparison.Ordinal));
    }

    private sealed record ExternalToolCall(
        string ExecutablePath,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout,
        int MaximumDevices);

    private sealed class FakeSadpProcessRunner(
        Func<ExternalToolCall, HikvisionSadpProcessResult> execute) : IHikvisionSadpProcessRunner
    {
        public Task<HikvisionSadpProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            int maximumDevices,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(execute(new ExternalToolCall(
                executablePath,
                arguments,
                timeout,
                maximumDevices)));
        }
    }

    private sealed class FragmentedTextReader(string value, int maximumChunkSize) : TextReader
    {
        private int _offset;

        internal bool ReadToEndCalled { get; private set; }
        internal bool ReadLineCalled { get; private set; }
        internal int SingleCharacterReads { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            if (_offset >= value.Length)
            {
                return 0;
            }
            var copied = Math.Min(Math.Min(count, maximumChunkSize), value.Length - _offset);
            value.CopyTo(_offset, buffer, index, copied);
            _offset += copied;
            return copied;
        }

        public override int Read()
        {
            SingleCharacterReads++;
            if (_offset >= value.Length)
            {
                return -1;
            }
            return value[_offset++];
        }

        public override string ReadToEnd()
        {
            ReadToEndCalled = true;
            throw new InvalidOperationException("SADP output must be parsed as a stream.");
        }

        public override string? ReadLine()
        {
            ReadLineCalled = true;
            throw new InvalidOperationException("SADP output must be parsed as a stream.");
        }
    }

    private sealed class FixedConflictProbe(bool hiToolsRunning, bool udpListenerPresent)
        : IHikvisionSadpConflictProbe
    {
        public HikvisionSadpConflictState Probe() => new(hiToolsRunning, udpListenerPresent);
    }

    private sealed class AvailableToolVerifier : IHikvisionSadpToolVerifier
    {
        public Task<string?> VerifyAsync(string executablePath, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FixedDiscoveryService(IpcDiscoveryScanResult result) : IIpcDiscoveryService
    {
        public Task<IpcDiscoveryScanResult> DiscoverAsync(
            IpcDiscoveryRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ThrowingDiscoveryService : IIpcDiscoveryService
    {
        public Task<IpcDiscoveryScanResult> DiscoverAsync(
            IpcDiscoveryRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("raw details must not escape");
    }

    private static async Task AssertSadpProcessTreeIsTerminatedAsync(bool cancelCaller)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var powershell = GetWindowsPowerShellPath();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "UnpackVision.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var childPidPath = Path.Combine(temporaryDirectory, "sadp-child.pid");
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
                try
                {
                    await new StreamingHikvisionSadpProcessRunner().RunAsync(
                        powershell,
                        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(parentCommand)],
                        // The caller cancellation remains the test deadline. Keep the runner
                        // timeout longer so CodeQL instrumentation cannot preempt the child
                        // startup and turn this process-tree security test into a false failure.
                        TimeSpan.FromSeconds(60),
                        maximumDevices: 64,
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // Both a cancellation exception and a completed termination result are valid.
                    // The assertion below verifies the actual security contract: no child survives.
                }
            }
            else
            {
                var result = await new StreamingHikvisionSadpProcessRunner().RunAsync(
                    powershell,
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(parentCommand)],
                    deadline,
                    maximumDevices: 64);
                Assert.True(result.Started);
                Assert.True(result.TimedOut);
                Assert.Null(result.ExitCode);
            }

            Assert.True(File.Exists(childPidPath), "The SADP helper did not start its child before termination.");
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Delayed antivirus handles must not hide the process-tree assertion.
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

        Assert.Fail($"SADP child process {processId} remained alive after its parent was terminated.");
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The child already exited or raced with cleanup.
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

    private static string SadpXml(
        string address,
        string deviceType,
        string serial,
        string mac,
        int httpPort,
        int channels) => $"""
        Discovering Hikvision devices via SADP protocol...
        <?xml version="1.0" encoding="UTF-8"?>
        <SADPDeviceList version="2.0">
          <ProbeMatch>
            <DeviceType>{deviceType}</DeviceType>
            <DeviceSN>{serial}</DeviceSN>
            <MAC>{mac}</MAC>
            <IPv4Address>{address}</IPv4Address>
            <CommandPort>8000</CommandPort>
            <HttpPort>{httpPort}</HttpPort>
            <AnalogChannelNum>{channels}</AnalogChannelNum>
            <DigitalChannelNum>0</DigitalChannelNum>
          </ProbeMatch>
        </SADPDeviceList>
        """;
}
