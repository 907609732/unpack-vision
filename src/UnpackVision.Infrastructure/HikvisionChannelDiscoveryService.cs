using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Discovers recorder streams through Hikvision ISAPI. Each request owns its HTTP handler so
/// credentials cannot leak into a shared connection pool or be reused for a different recorder.
/// Redirects are disabled for the same reason.
/// </summary>
public sealed class HikvisionChannelDiscoveryService : IHikvisionChannelDiscoveryService
{
    private const int MaximumResponseBytes = 512 * 1024;
    private readonly TimeSpan _timeout;

    public HikvisionChannelDiscoveryService(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<IReadOnlyList<HikvisionChannelDescriptor>> DiscoverAsync(
        HikvisionDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = BuildEndpoint(request.Host, request.HttpPort);
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            throw new ArgumentException("海康录像机账号不能为空", nameof(request));
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            Credentials = new NetworkCredential(request.Username.Trim(), request.Password),
            PreAuthenticate = true,
            UseDefaultCredentials = false
        };
        using var client = new HttpClient(handler) { Timeout = _timeout };
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Accept.ParseAdd("application/xml");
        using var response = await client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("海康录像机拒绝登录，请检查账号和密码");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("录像机没有开放 ISAPI 通道列表，请检查设备型号和管理端口");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"录像机通道发现失败（HTTP {(int)response.StatusCode}）");
        }
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("录像机返回的通道列表超过安全大小限制");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var xml = await ReadLimitedAsync(stream, cancellationToken).ConfigureAwait(false);
        return HikvisionChannelDiscoveryParser.Parse(xml);
    }

    internal static Uri BuildEndpoint(string host, int httpPort)
    {
        if (httpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(httpPort), "海康管理端口必须在 1 到 65535 之间");
        }

        var value = host?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "http://", "https://", "rtsp://" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        if (!Uri.TryCreate($"http://{value}", UriKind.Absolute, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            parsed.AbsolutePath != "/" ||
            parsed.Port != 80 ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new ArgumentException("海康录像机地址只能填写 IP 地址或主机名", nameof(host));
        }

        return new UriBuilder(Uri.UriSchemeHttp, parsed.Host, httpPort, "/ISAPI/Streaming/channels").Uri;
    }

    private static async Task<string> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (memory.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("录像机返回的通道列表超过安全大小限制");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        memory.Position = 0;
        using var reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class HikvisionChannelDiscoveryParser
{
    public static IReadOnlyList<HikvisionChannelDescriptor> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 512 * 1024
        };
        using var textReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var streams = new Dictionary<int, (HikvisionStreamDescriptor? Main, HikvisionStreamDescriptor? Sub)>();

        foreach (var channelElement in document.Descendants().Where(element =>
                     element.Name.LocalName.Equals("StreamingChannel", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryInt(Value(channelElement, "id"), out var streamId) || streamId < 101)
            {
                continue;
            }
            var enabled = Value(channelElement, "enabled");
            if (!string.IsNullOrWhiteSpace(enabled) &&
                (!bool.TryParse(enabled, out var isEnabled) || !isEnabled))
            {
                continue;
            }

            var streamKind = streamId % 100;
            if (streamKind is not 1 and not 2)
            {
                continue;
            }
            var channelNumber = streamId / 100;
            var descriptor = new HikvisionStreamDescriptor(
                streamId,
                Value(channelElement, "videoCodecType") ?? string.Empty,
                ParseInt(Value(channelElement, "videoResolutionWidth")),
                ParseInt(Value(channelElement, "videoResolutionHeight")),
                ParseFrameRate(Value(channelElement, "maxFrameRate")));

            streams.TryGetValue(channelNumber, out var current);
            streams[channelNumber] = streamKind == 1
                ? (descriptor, current.Sub)
                : (current.Main, descriptor);
        }

        return streams
            .OrderBy(pair => pair.Key)
            .Select(pair => new HikvisionChannelDescriptor(pair.Key, pair.Value.Main, pair.Value.Sub))
            .Where(channel => channel.HasAnyStream)
            .ToArray();
    }

    private static string? Value(XElement element, string localName) =>
        element.Descendants().FirstOrDefault(child =>
            child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private static bool TryInt(string? value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static int ParseInt(string? value) => TryInt(value, out var parsed) ? parsed : 0;

    private static double ParseFrameRate(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return 0;
        }
        return parsed > 100 ? parsed / 100d : parsed;
    }
}
