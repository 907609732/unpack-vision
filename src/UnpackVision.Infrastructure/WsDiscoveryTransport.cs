using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

internal enum WsDiscoveryProbeKind
{
    NetworkVideoTransmitter,
    Untyped
}

internal sealed record WsDiscoveryProbeResult(
    IReadOnlyList<IpcDiscoveryDevice> Devices,
    IReadOnlyList<string> Notices);

internal interface IWsDiscoveryTransport
{
    Task<WsDiscoveryProbeResult> ProbeAsync(
        WsDiscoveryProbeKind kind,
        TimeSpan timeout,
        int maximumDevices,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends bounded WS-Discovery probes on selected private IPv4 LAN adapters. Each adapter
/// owns its socket and failure boundary so a virtual adapter, bad datagram, or driver error
/// cannot terminate discovery on another interface.
/// </summary>
internal sealed class WsDiscoveryUdpTransport : IWsDiscoveryTransport
{
    private const int MaximumDatagramBytes = 64 * 1024;
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);
    private readonly IWsDiscoveryNetworkAdapterProvider _adapterProvider;

    internal WsDiscoveryUdpTransport()
        : this(new SystemWsDiscoveryNetworkAdapterProvider())
    {
    }

    internal WsDiscoveryUdpTransport(IWsDiscoveryNetworkAdapterProvider adapterProvider)
    {
        _adapterProvider = adapterProvider ?? throw new ArgumentNullException(nameof(adapterProvider));
    }

    public async Task<WsDiscoveryProbeResult> ProbeAsync(
        WsDiscoveryProbeKind kind,
        TimeSpan timeout,
        int maximumDevices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = _adapterProvider.GetAdapters();
        if (adapters.Count == 0)
        {
            return new WsDiscoveryProbeResult(
                [],
                ["没有找到可用于 IPC 扫描的局域网网卡，请检查网卡是否启用并连接到专用网络。"]);
        }

        var messageId = $"urn:uuid:{Guid.NewGuid():D}";
        var probe = WsDiscoveryProbeMessage.Create(messageId, kind);
        var tasks = adapters
            .Select(adapter => ProbeAdapterAsync(
                adapter,
                probe,
                messageId,
                kind,
                timeout,
                maximumDevices,
                cancellationToken))
            .ToArray();

        WsDiscoveryAdapterProbeResult[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var devices = results
            .SelectMany(result => result.Devices)
            .Take(Math.Min(maximumDevices * 4, 1024))
            .ToArray();
        var notices = new List<string>();
        var failedAdapters = results.Count(result => result.Failed);
        var malformedResponses = results.Sum(result => result.MalformedResponses);
        if (failedAdapters > 0)
        {
            notices.Add(failedAdapters == adapters.Count
                ? "所有可用网卡都无法启动 IPC 扫描，请检查端口占用、防火墙和网络设置。"
                : $"有 {failedAdapters} 个网卡无法启动 IPC 扫描，已继续使用其他网卡；请检查端口占用或网络设置。");
        }
        if (malformedResponses > 0)
        {
            notices.Add($"扫描时已安全忽略 {Math.Min(malformedResponses, 999)} 个格式异常的设备响应。");
        }

        return new WsDiscoveryProbeResult(devices, notices);
    }

    private static async Task<WsDiscoveryAdapterProbeResult> ProbeAdapterAsync(
        WsDiscoveryNetworkAdapter adapter,
        byte[] probe,
        string messageId,
        WsDiscoveryProbeKind kind,
        TimeSpan timeout,
        int maximumDevices,
        CancellationToken callerCancellationToken)
    {
        var devices = new List<IpcDiscoveryDevice>();
        var malformedResponses = 0;
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var client = new UdpClient(AddressFamily.InterNetwork);
        using var closeOnCancellation = timeoutCancellation.Token.Register(static state =>
        {
            try
            {
                ((UdpClient)state!).Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Cancellation races with normal disposal; both paths own the same socket.
            }
        }, client);

        try
        {
            client.Client.ReceiveBufferSize = MaximumDatagramBytes;
            client.Client.SendBufferSize = 16 * 1024;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(adapter.Address, 0));
            client.Ttl = 1;
            client.MulticastLoopback = false;

            await client.SendAsync(probe, MulticastEndpoint, timeoutCancellation.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(75), timeoutCancellation.Token).ConfigureAwait(false);
            await client.SendAsync(probe, MulticastEndpoint, timeoutCancellation.Token).ConfigureAwait(false);

            while (!timeoutCancellation.IsCancellationRequested && devices.Count < maximumDevices)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await client.ReceiveAsync(timeoutCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (!callerCancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (!callerCancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (datagram.Buffer.Length == 0 || datagram.Buffer.Length > MaximumDatagramBytes)
                {
                    malformedResponses++;
                    continue;
                }

                var parsed = WsDiscoveryResponseParser.Parse(
                    datagram.Buffer,
                    datagram.RemoteEndPoint.Address,
                    messageId,
                    kind == WsDiscoveryProbeKind.Untyped
                        ? IpcDiscoverySource.CompatibilityProbe
                        : IpcDiscoverySource.OnvifWsDiscovery);
                if (!parsed.IsValid)
                {
                    malformedResponses++;
                    continue;
                }

                foreach (var device in parsed.Devices)
                {
                    devices.Add(device);
                    if (devices.Count >= maximumDevices)
                    {
                        break;
                    }
                }
            }

            callerCancellationToken.ThrowIfCancellationRequested();
            return new WsDiscoveryAdapterProbeResult(devices, malformedResponses, Failed: false);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerCancellationToken);
        }
        catch (SocketException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerCancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new WsDiscoveryAdapterProbeResult(devices, malformedResponses, Failed: false);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return new WsDiscoveryAdapterProbeResult(devices, malformedResponses, Failed: true);
        }
    }

    private sealed record WsDiscoveryAdapterProbeResult(
        IReadOnlyList<IpcDiscoveryDevice> Devices,
        int MalformedResponses,
        bool Failed);
}

internal sealed record WsDiscoveryNetworkAdapter(
    string Id,
    string Name,
    IPAddress Address,
    bool HasDefaultGateway);

internal sealed record WsDiscoveryNetworkAdapterCandidate(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus OperationalStatus,
    IPAddress Address,
    bool HasDefaultGateway);

internal interface IWsDiscoveryNetworkAdapterProvider
{
    IReadOnlyList<WsDiscoveryNetworkAdapter> GetAdapters();
}

internal sealed class SystemWsDiscoveryNetworkAdapterProvider : IWsDiscoveryNetworkAdapterProvider
{
    public IReadOnlyList<WsDiscoveryNetworkAdapter> GetAdapters()
    {
        var candidates = new List<WsDiscoveryNetworkAdapterCandidate>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var hasDefaultGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                candidates.Add(new WsDiscoveryNetworkAdapterCandidate(
                    networkInterface.Id,
                    networkInterface.Name,
                    networkInterface.Description,
                    networkInterface.NetworkInterfaceType,
                    networkInterface.OperationalStatus,
                    unicast.Address,
                    hasDefaultGateway));
            }
        }

        return WsDiscoveryNetworkAdapterSelector.Select(candidates);
    }
}

internal static class WsDiscoveryNetworkAdapterSelector
{
    private const int MaximumAdapters = 16;
    private static readonly string[] VirtualAdapterMarkers =
    [
        "hyper-v", "vethernet", "virtual", "vmware", "virtualbox", "wsl",
        "npcap", "loopback", "docker", "meta", "tunnel", "wintun", " tap", " tun"
    ];

    internal static IReadOnlyList<WsDiscoveryNetworkAdapter> Select(
        IEnumerable<WsDiscoveryNetworkAdapterCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(IsEligible)
            .GroupBy(candidate => candidate.Address)
            .Select(group => group
                .OrderByDescending(candidate => candidate.HasDefaultGateway)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(candidate => candidate.HasDefaultGateway)
            .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumAdapters)
            .Select(candidate => new WsDiscoveryNetworkAdapter(
                candidate.Id,
                candidate.Name,
                candidate.Address,
                candidate.HasDefaultGateway))
            .ToArray();
    }

    private static bool IsEligible(WsDiscoveryNetworkAdapterCandidate candidate)
    {
        if (candidate.OperationalStatus != OperationalStatus.Up ||
            !IsLanType(candidate.InterfaceType) ||
            !IsRfc1918(candidate.Address))
        {
            return false;
        }

        var identity = $"{candidate.Id} {candidate.Name} {candidate.Description}";
        return !VirtualAdapterMarkers.Any(marker =>
            identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLanType(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Ethernet or
        NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.FastEthernetT or
        NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.Wireless80211;

    private static bool IsRfc1918(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }
}

internal static class WsDiscoveryProbeMessage
{
    internal static byte[] Create(string messageId, WsDiscoveryProbeKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var types = kind == WsDiscoveryProbeKind.NetworkVideoTransmitter
            ? "<d:Types>dn:NetworkVideoTransmitter</d:Types>"
            : string.Empty;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <e:Header>
                <w:MessageID>{messageId}</w:MessageID>
                <w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
                <w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
              </e:Header>
              <e:Body><d:Probe>{types}</d:Probe></e:Body>
            </e:Envelope>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }
}

internal sealed record WsDiscoveryParsedResponse(
    bool IsValid,
    IReadOnlyList<IpcDiscoveryDevice> Devices)
{
    internal static WsDiscoveryParsedResponse Invalid { get; } = new(false, []);
}

internal static class WsDiscoveryResponseParser
{
    private const int MaximumDatagramBytes = 64 * 1024;

    internal static WsDiscoveryParsedResponse Parse(
        ReadOnlyMemory<byte> datagram,
        IPAddress remoteAddress,
        string expectedMessageId,
        IpcDiscoverySource source)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        if (datagram.IsEmpty || datagram.Length > MaximumDatagramBytes ||
            string.IsNullOrWhiteSpace(expectedMessageId))
        {
            return WsDiscoveryParsedResponse.Invalid;
        }

        try
        {
            using var stream = new MemoryStream(datagram.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = MaximumDatagramBytes * 2L
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var envelope = document.Root;
            var header = envelope?.Elements().FirstOrDefault(element => element.Name.LocalName == "Header");
            var body = envelope?.Elements().FirstOrDefault(element => element.Name.LocalName == "Body");
            var relatesTo = header?.Elements().FirstOrDefault(element => element.Name.LocalName == "RelatesTo")
                ?.Value.Trim();
            if (body is null || !string.Equals(relatesTo, expectedMessageId, StringComparison.OrdinalIgnoreCase))
            {
                return WsDiscoveryParsedResponse.Invalid;
            }

            var devices = new List<IpcDiscoveryDevice>();
            foreach (var probeMatch in body.Descendants().Where(element => element.Name.LocalName == "ProbeMatch").Take(256))
            {
                var xAddresses = probeMatch.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "XAddrs")
                    ?.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(32)
                    .ToArray() ?? [];
                if (xAddresses.Length == 0)
                {
                    continue;
                }

                var endpointReference = probeMatch.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "EndpointReference")
                    ?.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Address")
                    ?.Value.Trim() ?? string.Empty;
                var scopes = probeMatch.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Scopes")
                    ?.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(32)
                    .ToArray() ?? [];
                devices.Add(new IpcDiscoveryDevice(
                    endpointReference,
                    ParseScopeValue(scopes, "name"),
                    ParseScopeValue(scopes, "manufacturer"),
                    ParseScopeValue(scopes, "hardware"),
                    remoteAddress.ToString(),
                    xAddresses,
                    scopes)
                {
                    Source = source
                });
            }

            return new WsDiscoveryParsedResponse(true, devices);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
        {
            return WsDiscoveryParsedResponse.Invalid;
        }
    }

    private static string ParseScopeValue(IEnumerable<string> scopes, string segment)
    {
        var marker = $"/{segment}/";
        foreach (var scope in scopes.Take(32))
        {
            var index = scope.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }
            var value = scope[(index + marker.Length)..];
            try
            {
                return Uri.UnescapeDataString(value).Trim();
            }
            catch (UriFormatException)
            {
                return string.Empty;
            }
        }
        return string.Empty;
    }
}
