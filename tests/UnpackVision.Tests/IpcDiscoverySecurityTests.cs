using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class IpcDiscoverySecurityTests
{
    [Fact]
    public void NormalizerAcceptsPrivateHttpHttpsAndUniqueLocalIpv6Addresses()
    {
        var result = IpcDiscoveryResultNormalizer.Normalize(
        [
            Device(
                "urn:uuid:private-camera",
                "192.168.31.64",
                "http://192.168.31.64/onvif/device_service"),
            Device(
                "urn:uuid:private-camera-2",
                "10.0.0.8",
                "https://10.0.0.8:8443/onvif/device_service"),
            Device(
                "urn:uuid:private-camera-3",
                "172.16.4.20",
                "http://172.16.4.20:8080/onvif/device_service"),
            Device(
                "urn:uuid:private-camera-4",
                "fd12:3456:789a::20",
                "https://[fd12:3456:789a::20]:7443/onvif/device_service")
        ]);

        Assert.Equal(4, result.Count);
        var addresses = result.SelectMany(device => device.Addresses).ToArray();
        Assert.Contains(addresses, address => address.StartsWith("http://192.168.31.64", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(addresses, address => address.StartsWith("https://10.0.0.8:8443", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(addresses, address => address.StartsWith("http://172.16.4.20:8080", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(addresses, address => address.Contains("[fd12:3456:789a::20]", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("http://admin:secret@192.168.31.64/onvif/device_service", "192.168.31.64")]
    [InlineData("http://192.168.31.64/onvif/device_service?token=not-for-logs", "192.168.31.64")]
    [InlineData("rtsp://192.168.31.64:554/Streaming/Channels/101", "192.168.31.64")]
    [InlineData("file:///C:/Windows/win.ini", "192.168.31.64")]
    [InlineData("ftp://192.168.31.64/onvif/device_service", "192.168.31.64")]
    [InlineData("http://127.0.0.1/onvif/device_service", "127.0.0.1")]
    [InlineData("http://0.0.0.0/onvif/device_service", "0.0.0.0")]
    [InlineData("http://239.255.255.250:3702/onvif/device_service", "239.255.255.250")]
    [InlineData("http://8.8.8.8/onvif/device_service", "8.8.8.8")]
    [InlineData("http://[::1]/onvif/device_service", "::1")]
    [InlineData("http://[ff02::c]/onvif/device_service", "ff02::c")]
    [InlineData("http://[2001:4860:4860::8888]/onvif/device_service", "2001:4860:4860::8888")]
    [InlineData("http://192.168.31.64/onvif/device_service\r\nInjected: true", "192.168.31.64")]
    public void NormalizerRejectsUnsafeOrNonLanAddresses(string address, string remoteAddress)
    {
        var result = IpcDiscoveryResultNormalizer.Normalize(
            [Device("urn:uuid:unsafe-camera", remoteAddress, address)]);

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizerRejectsUnreasonablyLongAddressBeforeItCanReachUiOrLogs()
    {
        var address = "http://192.168.31.64/onvif/" + new string('a', 4096);

        var result = IpcDiscoveryResultNormalizer.Normalize(
            [Device("urn:uuid:oversized-camera", "192.168.31.64", address)]);

        Assert.Empty(result);
    }

    [Fact]
    public void NormalizerMergesDuplicateResponsesFromMultipleNetworkInterfaces()
    {
        var result = IpcDiscoveryResultNormalizer.Normalize(
        [
            Device(
                "URN:UUID:camera-1",
                "192.168.31.64",
                "HTTP://192.168.31.64:80/onvif/device_service"),
            Device(
                "urn:uuid:camera-1",
                "192.168.31.64",
                "http://192.168.31.64/onvif/device_service",
                "https://192.168.31.64:443/onvif/device_service")
        ]);

        var camera = Assert.Single(result);
        Assert.Equal(2, camera.Addresses.Count);
        Assert.Single(camera.Addresses, address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        Assert.Single(camera.Addresses, address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizerPreservesTheDiscoverySource()
    {
        var result = IpcDiscoveryResultNormalizer.Normalize(
        [
            Device(
                "urn:uuid:compatibility-camera",
                "192.168.31.64",
                "http://192.168.31.64/onvif/device_service") with
            {
                Source = IpcDiscoverySource.CompatibilityProbe
            }
        ]);

        Assert.Equal(IpcDiscoverySource.CompatibilityProbe, Assert.Single(result).Source);
    }

    [Fact]
    public void NormalizerKeepsDistinctDevicesSeenOnDifferentInterfaces()
    {
        var result = IpcDiscoveryResultNormalizer.Normalize(
        [
            Device("urn:uuid:camera-a", "192.168.31.64", "http://192.168.31.64/onvif/device_service"),
            Device("urn:uuid:camera-b", "10.10.0.64", "http://10.10.0.64/onvif/device_service")
        ]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NormalizerBoundsDeviceAndPerDeviceAddressCounts()
    {
        var devices = Enumerable.Range(1, 100)
            .Select(index =>
            {
                var remoteAddress = $"10.{index % 200 + 1}.40.64";
                var addresses = Enumerable.Range(1, 20)
                    .Select(port => $"http://{remoteAddress}:{8000 + port}/onvif/device_service")
                    .ToArray();
                return Device($"urn:uuid:camera-{index}", remoteAddress, addresses);
            })
            .ToArray();

        var result = IpcDiscoveryResultNormalizer.Normalize(
            devices,
            maximumDevices: 64,
            maximumAddressesPerDevice: 8);

        Assert.Equal(64, result.Count);
        Assert.All(result, device => Assert.InRange(device.Addresses.Count, 1, 8));
    }

    [Fact]
    public void DiscoveryContractsRequireBoundedTimeoutCancellationAndNoCredentials()
    {
        var request = new IpcDiscoveryRequest(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), request.EffectiveTimeout);
        Assert.Equal(64, request.MaximumDevices);
        Assert.Equal(
            IpcDiscoveryRequest.DefaultTimeout,
            new IpcDiscoveryRequest(TimeSpan.Zero).EffectiveTimeout);

        var discover = typeof(IIpcDiscoveryService).GetMethod(nameof(IIpcDiscoveryService.DiscoverAsync));
        Assert.NotNull(discover);
        Assert.Contains(discover.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));

        var forbiddenTerms = new[] { "password", "credential", "secret", "token", "username" };
        Assert.DoesNotContain(
            typeof(IpcDiscoveryRequest).GetProperties(),
            property => forbiddenTerms.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(IpcDiscoveryDevice).GetProperties(),
            property => forbiddenTerms.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            typeof(Task<IpcDiscoveryScanResult>),
            discover.ReturnType);
        Assert.Equal(
            [
                IpcDiscoverySource.OnvifWsDiscovery,
                IpcDiscoverySource.CompatibilityProbe,
                IpcDiscoverySource.HikvisionSadp
            ],
            Enum.GetValues<IpcDiscoverySource>());
    }

    [Fact]
    public async Task DiscoveryInterfaceCanPropagateCallerCancellation()
    {
        IIpcDiscoveryService service = new BlockingDiscoveryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(3)), cancellation.Token));
    }

    [Fact]
    public async Task OnvifDiscoveryRejectsUnboundedTimeoutsBeforeOpeningSockets()
    {
        var service = new OnvifIpcDiscoveryService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromMilliseconds(999))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(16))));
    }

    [Fact]
    public async Task OnvifDiscoveryTreatsZeroTimeoutAsTheBoundedPublicDefault()
    {
        var device = Device(
            "urn:uuid:onvif-camera",
            "192.168.31.64",
            "http://192.168.31.64/onvif/device_service");
        var transport = new ScriptedDiscoveryTransport(new WsDiscoveryProbeResult([device], []));
        var service = new OnvifIpcDiscoveryService(transport);

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.Zero));

        Assert.Single(result.Devices);
        Assert.Equal([TimeSpan.FromSeconds(2.5)], transport.ProbeTimeouts);
    }

    [Fact]
    public void CompositeMergeKeepsLegacySourceAndReportsEveryContributingSource()
    {
        var onvif = Device(
            "urn:uuid:onvif-camera",
            "192.168.31.64",
            "http://192.168.31.64/onvif/device_service");
        var sadp = Device(
            string.Empty,
            "192.168.31.64",
            "http://192.168.31.64/") with
        {
            Source = IpcDiscoverySource.HikvisionSadp
        };

        var merged = Assert.Single(CompositeIpcDiscoveryService.MergeDevices([onvif, sadp], 64));

        Assert.Equal(IpcDiscoverySource.HikvisionSadp, merged.Source);
        Assert.Equal(
            [IpcDiscoverySource.OnvifWsDiscovery, IpcDiscoverySource.HikvisionSadp],
            merged.Sources);
    }

    [Fact]
    public void DeviceDeserializesLegacySourceWithoutTheAdditiveSourcesField()
    {
        const string legacyJson = """
            {
              "EndpointReference": "",
              "DisplayName": "Camera",
              "Manufacturer": "Hikvision",
              "Model": "Model",
              "RemoteAddress": "192.168.31.64",
              "Addresses": ["http://192.168.31.64/"],
              "Scopes": [],
              "Source": 2
            }
            """;

        var device = JsonSerializer.Deserialize<IpcDiscoveryDevice>(legacyJson);

        Assert.NotNull(device);
        Assert.Equal(IpcDiscoverySource.HikvisionSadp, device.Source);
        Assert.Equal([IpcDiscoverySource.HikvisionSadp], device.Sources);
    }

    [Fact]
    public async Task OnvifDiscoveryHonorsAnAlreadyCanceledCallerToken()
    {
        var service = new OnvifIpcDiscoveryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(3)), cancellation.Token));
    }

    [Fact]
    public async Task OnvifDiscoveryFallsBackToUntypedProbeOnlyAfterStandardProbeReturnsNoSafeDevice()
    {
        var compatibilityDevice = Device(
            "urn:uuid:compatibility-camera",
            "192.168.31.64",
            "http://192.168.31.64/onvif/device_service") with
        {
            Source = IpcDiscoverySource.CompatibilityProbe
        };
        var transport = new ScriptedDiscoveryTransport(
            new WsDiscoveryProbeResult([], []),
            new WsDiscoveryProbeResult([compatibilityDevice], ["兼容扫描测试通知"]));
        var service = new OnvifIpcDiscoveryService(transport);

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(2)));

        Assert.Equal(
            [WsDiscoveryProbeKind.NetworkVideoTransmitter, WsDiscoveryProbeKind.Untyped],
            transport.ProbeKinds);
        Assert.Equal(IpcDiscoverySource.CompatibilityProbe, Assert.Single(result.Devices).Source);
        Assert.Contains(result.Notices, notice => notice.Contains("兼容探测", StringComparison.Ordinal));
        Assert.Contains("兼容扫描测试通知", result.Notices);
    }

    [Fact]
    public async Task OnvifDiscoveryDoesNotUseCompatibilityProbeWhenStandardProbeReturnsSafeDevice()
    {
        var standardDevice = Device(
            "urn:uuid:onvif-camera",
            "192.168.31.64",
            "http://192.168.31.64/onvif/device_service");
        var transport = new ScriptedDiscoveryTransport(
            new WsDiscoveryProbeResult([standardDevice], []),
            new WsDiscoveryProbeResult([], []));
        var service = new OnvifIpcDiscoveryService(transport);

        var result = await service.DiscoverAsync(new IpcDiscoveryRequest(TimeSpan.FromSeconds(2)));

        Assert.Equal([WsDiscoveryProbeKind.NetworkVideoTransmitter], transport.ProbeKinds);
        Assert.Equal(IpcDiscoverySource.OnvifWsDiscovery, Assert.Single(result.Devices).Source);
    }

    [Fact]
    public void AdapterSelectorPrefersPhysicalPrivateLanAndRejectsVirtualApipaAndPublicAdapters()
    {
        var candidates = new[]
        {
            Adapter("physical-no-gateway", "专用摄像头网卡", "Intel Ethernet", "192.168.50.2", false),
            Adapter("physical-with-gateway", "以太网", "Intel Ethernet", "192.168.1.6", true),
            Adapter("hyper-v", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet", "172.26.112.1", false),
            Adapter("npcap", "以太网 3", "Npcap Loopback Adapter", "169.254.75.83", false),
            Adapter("meta", "Meta", "Meta Tunnel", "10.10.10.1", false),
            Adapter("public", "外部网卡", "Intel Ethernet", "198.51.100.2", true),
            Adapter("down", "未连接网卡", "Intel Ethernet", "192.168.60.2", true, OperationalStatus.Down)
        };

        var result = WsDiscoveryNetworkAdapterSelector.Select(candidates);

        Assert.Equal(2, result.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.6"), result[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.50.2"), result[1].Address);
    }

    [Fact]
    public void ResponseParserAcceptsMatchingProbeResponseAndExtractsOnlyBoundedMetadata()
    {
        const string messageId = "urn:uuid:11111111-1111-1111-1111-111111111111";
        var xml = ProbeMatch(
            messageId,
            "urn:uuid:camera-1",
            "http://192.168.31.64/onvif/device_service",
            "onvif://www.onvif.org/name/Front%20Door onvif://www.onvif.org/hardware/TestCam");

        var parsed = WsDiscoveryResponseParser.Parse(
            Encoding.UTF8.GetBytes(xml),
            IPAddress.Parse("192.168.31.64"),
            messageId,
            IpcDiscoverySource.CompatibilityProbe);

        Assert.True(parsed.IsValid);
        var device = Assert.Single(parsed.Devices);
        Assert.Equal("Front Door", device.DisplayName);
        Assert.Equal("TestCam", device.Model);
        Assert.Equal(IpcDiscoverySource.CompatibilityProbe, device.Source);
    }

    [Fact]
    public void ProbeMessagesNeverCarryCredentialsAndCompatibilityProbeIsUntyped()
    {
        const string messageId = "urn:uuid:44444444-4444-4444-4444-444444444444";
        var standard = Encoding.UTF8.GetString(
            WsDiscoveryProbeMessage.Create(messageId, WsDiscoveryProbeKind.NetworkVideoTransmitter));
        var compatibility = Encoding.UTF8.GetString(
            WsDiscoveryProbeMessage.Create(messageId, WsDiscoveryProbeKind.Untyped));

        Assert.Contains("dn:NetworkVideoTransmitter", standard, StringComparison.Ordinal);
        Assert.DoesNotContain("<d:Types>", compatibility, StringComparison.Ordinal);
        foreach (var payload in new[] { standard, compatibility })
        {
            Assert.Contains(messageId, payload, StringComparison.Ordinal);
            Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ResponseParserRejectsWrongRelatesToMalformedXmlDtdAndOversizedDatagrams()
    {
        const string expectedMessageId = "urn:uuid:22222222-2222-2222-2222-222222222222";
        var wrongMessage = ProbeMatch(
            "urn:uuid:33333333-3333-3333-3333-333333333333",
            "urn:uuid:camera-2",
            "http://192.168.31.64/onvif/device_service",
            string.Empty);
        const string dtd = """
            <!DOCTYPE x [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]>
            <Envelope><Header><RelatesTo>urn:uuid:22222222-2222-2222-2222-222222222222</RelatesTo></Header>
            <Body><ProbeMatch><XAddrs>&secret;</XAddrs></ProbeMatch></Body></Envelope>
            """;

        Assert.False(Parse(wrongMessage, expectedMessageId).IsValid);
        Assert.False(Parse("<Envelope>", expectedMessageId).IsValid);
        Assert.False(Parse(dtd, expectedMessageId).IsValid);
        Assert.False(WsDiscoveryResponseParser.Parse(
            new byte[(64 * 1024) + 1],
            IPAddress.Parse("192.168.31.64"),
            expectedMessageId,
            IpcDiscoverySource.OnvifWsDiscovery).IsValid);
    }

    [Fact]
    public async Task UdpTransportKeepsHealthyAdapterAliveWhenAnotherAdapterCannotBind()
    {
        var provider = new FixedAdapterProvider(
        [
            new WsDiscoveryNetworkAdapter("bad", "bad", IPAddress.Parse("192.0.2.123"), false),
            new WsDiscoveryNetworkAdapter("loopback-test", "loopback-test", IPAddress.Loopback, false)
        ]);
        var transport = new WsDiscoveryUdpTransport(provider);

        var result = await transport.ProbeAsync(
            WsDiscoveryProbeKind.Untyped,
            TimeSpan.FromMilliseconds(150),
            maximumDevices: 4,
            CancellationToken.None);

        // This adapter-isolation test intentionally opens a real loopback UDP socket.
        // An ambient WS-Discovery responder on the test machine may legitimately reply
        // to this probe's MessageID; device trust is covered by the normalizer tests.
        Assert.All(result.Devices, device =>
            Assert.Equal(IPAddress.Loopback, IPAddress.Parse(device.RemoteAddress)));
        Assert.Contains(result.Notices, notice =>
            notice.Contains("1 个网卡", StringComparison.Ordinal) &&
            notice.Contains("端口占用", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UdpTransportClosesSocketsPromptlyWhenCallerCancels()
    {
        var provider = new FixedAdapterProvider(
        [
            new WsDiscoveryNetworkAdapter("loopback-test", "loopback-test", IPAddress.Loopback, false)
        ]);
        var transport = new WsDiscoveryUdpTransport(provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.ProbeAsync(
                WsDiscoveryProbeKind.NetworkVideoTransmitter,
                TimeSpan.FromSeconds(5),
                maximumDevices: 4,
                cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"取消耗时 {stopwatch.Elapsed}。");
    }

    private static IpcDiscoveryDevice Device(
        string endpointReference,
        string remoteAddress,
        params string[] addresses) =>
        new(
            endpointReference,
            "测试摄像头",
            "Test Manufacturer",
            "Test Model",
            remoteAddress,
            addresses,
            ["onvif://www.onvif.org/type/video_encoder"]);

    private static WsDiscoveryNetworkAdapterCandidate Adapter(
        string id,
        string name,
        string description,
        string address,
        bool hasDefaultGateway,
        OperationalStatus status = OperationalStatus.Up) =>
        new(
            id,
            name,
            description,
            NetworkInterfaceType.Ethernet,
            status,
            IPAddress.Parse(address),
            hasDefaultGateway);

    private static WsDiscoveryParsedResponse Parse(string xml, string messageId) =>
        WsDiscoveryResponseParser.Parse(
            Encoding.UTF8.GetBytes(xml),
            IPAddress.Parse("192.168.31.64"),
            messageId,
            IpcDiscoverySource.OnvifWsDiscovery);

    private static string ProbeMatch(
        string relatesTo,
        string endpointReference,
        string xAddress,
        string scopes) =>
        $$"""
          <?xml version="1.0" encoding="UTF-8"?>
          <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                      xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                      xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
            <e:Header><w:RelatesTo>{{relatesTo}}</w:RelatesTo></e:Header>
            <e:Body>
              <d:ProbeMatches>
                <d:ProbeMatch>
                  <w:EndpointReference><w:Address>{{endpointReference}}</w:Address></w:EndpointReference>
                  <d:Scopes>{{scopes}}</d:Scopes>
                  <d:XAddrs>{{xAddress}}</d:XAddrs>
                </d:ProbeMatch>
              </d:ProbeMatches>
            </e:Body>
          </e:Envelope>
          """;

    private sealed class BlockingDiscoveryService : IIpcDiscoveryService
    {
        public async Task<IpcDiscoveryScanResult> DiscoverAsync(
            IpcDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new IpcDiscoveryScanResult([], []);
        }
    }

    private sealed class ScriptedDiscoveryTransport(params WsDiscoveryProbeResult[] results)
        : IWsDiscoveryTransport
    {
        private readonly Queue<WsDiscoveryProbeResult> _results = new(results);

        internal List<WsDiscoveryProbeKind> ProbeKinds { get; } = [];

        internal List<TimeSpan> ProbeTimeouts { get; } = [];

        public Task<WsDiscoveryProbeResult> ProbeAsync(
            WsDiscoveryProbeKind kind,
            TimeSpan timeout,
            int maximumDevices,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeKinds.Add(kind);
            ProbeTimeouts.Add(timeout);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FixedAdapterProvider(IReadOnlyList<WsDiscoveryNetworkAdapter> adapters)
        : IWsDiscoveryNetworkAdapterProvider
    {
        public IReadOnlyList<WsDiscoveryNetworkAdapter> GetAdapters() => adapters;
    }
}
