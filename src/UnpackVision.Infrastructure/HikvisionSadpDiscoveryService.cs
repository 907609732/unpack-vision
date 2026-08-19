using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Discovers Hikvision devices through the hash-pinned, MIT-licensed hikvision-tooling CLI.
/// The adapter invokes only the read-only discover:sadp command and never forwards the
/// serial number or MAC address fields emitted by the upstream XML format.
/// </summary>
public sealed class HikvisionSadpDiscoveryService : IIpcDiscoveryService
{
    internal const string ToolVersion = "1.0.43";
    internal const string ExpectedExecutableSha256 =
        "64d162d55671d5267a2a435c69018866090b874dde9b5d35784eca828b8c6413";
    internal const int MaximumDiscoveryOutputCharacters = 60 * 1024;

    private readonly string _executablePath;
    private readonly IHikvisionSadpProcessRunner _processRunner;
    private readonly IHikvisionSadpConflictProbe _conflictProbe;
    private readonly IHikvisionSadpToolVerifier _toolVerifier;

    public HikvisionSadpDiscoveryService(string? executablePath = null)
        : this(
            executablePath ?? HikvisionSadpToolLocator.GetPackagedExecutablePath(),
            new StreamingHikvisionSadpProcessRunner(),
            new SystemHikvisionSadpConflictProbe(),
            new HashPinnedHikvisionSadpToolVerifier())
    {
    }

    internal HikvisionSadpDiscoveryService(
        string executablePath,
        IHikvisionSadpProcessRunner processRunner,
        IHikvisionSadpConflictProbe conflictProbe,
        IHikvisionSadpToolVerifier toolVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _conflictProbe = conflictProbe ?? throw new ArgumentNullException(nameof(conflictProbe));
        _toolVerifier = toolVerifier ?? throw new ArgumentNullException(nameof(toolVerifier));
    }

    public async Task<IpcDiscoveryScanResult> DiscoverAsync(
        IpcDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var notices = new List<string>(capacity: 3);
        var conflict = _conflictProbe.Probe();
        if (conflict.HiToolsDeliveryRunning || conflict.Udp37020ListenerPresent)
        {
            notices.Add("检测到海康交付助手或 UDP 37020 正在使用；SADP 扫描结果可能不完整。");
        }

        var availability = await _toolVerifier.VerifyAsync(_executablePath, cancellationToken);
        if (availability is not null)
        {
            notices.Add(availability);
            return new IpcDiscoveryScanResult([], notices);
        }

        var timeoutSeconds = Math.Clamp((int)Math.Ceiling(request.EffectiveTimeout.TotalSeconds), 1, 15);
        var executionTimeout = TimeSpan.FromSeconds(timeoutSeconds + 3);
        var result = await _processRunner.RunAsync(
            _executablePath,
            ["discover:sadp", "-xml", "-timeout", $"{timeoutSeconds}s"],
            executionTimeout,
            request.MaximumDevices,
            cancellationToken);

        if (!result.Started)
        {
            notices.Add("无法启动海康 SADP 扫描组件；已保留其他发现方式的结果。");
            return new IpcDiscoveryScanResult([], notices);
        }
        if (result.TimedOut)
        {
            notices.Add("海康 SADP 扫描超时；未将 0 台解释为局域网中没有设备。");
            return new IpcDiscoveryScanResult([], notices);
        }
        if (result.OutputStatus == HikvisionSadpOutputStatus.TooLarge)
        {
            notices.Add("海康 SADP 扫描返回内容超过安全上限，结果已丢弃。");
            return new IpcDiscoveryScanResult([], notices);
        }
        if (result.ExitCode != 0)
        {
            notices.Add("海康 SADP 扫描组件未能完成；已保留其他发现方式的结果。");
            return new IpcDiscoveryScanResult([], notices);
        }
        if (result.OutputStatus != HikvisionSadpOutputStatus.Valid)
        {
            notices.Add("海康 SADP 扫描返回了无法验证的格式，结果已丢弃。");
            return new IpcDiscoveryScanResult([], notices);
        }

        var devices = result.Devices;
        if (devices.Count == 0)
        {
            notices.Add(conflict.HiToolsDeliveryRunning || conflict.Udp37020ListenerPresent
                ? "SADP 未返回设备；当前存在扫描冲突，因此 0 台不代表局域网中没有海康设备。"
                : "SADP 未返回设备；请确认设备在线并与电脑处于同一局域网。");
        }
        return new IpcDiscoveryScanResult(devices, notices);
    }

    private static void ValidateRequest(IpcDiscoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EffectiveTimeout < TimeSpan.FromSeconds(1) ||
            request.EffectiveTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "IPC 扫描时间必须在 1 到 15 秒之间。");
        }
        if (request.MaximumDevices is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "IPC 扫描设备上限必须在 1 到 256 之间。");
        }
    }
}

internal interface IHikvisionSadpToolVerifier
{
    Task<string?> VerifyAsync(string executablePath, CancellationToken cancellationToken);
}

internal sealed class HashPinnedHikvisionSadpToolVerifier : IHikvisionSadpToolVerifier
{
    public async Task<string?> VerifyAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(executablePath))
        {
            return "海康 SADP 兼容扫描组件尚未安装；已继续使用 ONVIF 扫描。";
        }

        try
        {
            var file = new FileInfo(executablePath);
            if (file.Length is <= 0 or > 16 * 1024 * 1024)
            {
                return "海康 SADP 扫描组件大小异常，已拒绝运行。";
            }

            await using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash),
                Encoding.ASCII.GetBytes(HikvisionSadpDiscoveryService.ExpectedExecutableSha256))
                ? null
                : "海康 SADP 扫描组件完整性校验失败，已拒绝运行。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return "无法验证海康 SADP 扫描组件，已拒绝运行。";
        }
    }
}

internal static class HikvisionSadpToolLocator
{
    internal static string GetPackagedExecutablePath() => Path.Combine(
        AppContext.BaseDirectory,
        "runtimes",
        "hikvision-tooling",
        HikvisionSadpDiscoveryService.ToolVersion,
        "sadp.exe");
}

internal sealed record HikvisionSadpConflictState(
    bool HiToolsDeliveryRunning,
    bool Udp37020ListenerPresent);

internal interface IHikvisionSadpConflictProbe
{
    HikvisionSadpConflictState Probe();
}

internal sealed class SystemHikvisionSadpConflictProbe : IHikvisionSadpConflictProbe
{
    public HikvisionSadpConflictState Probe()
    {
        var hiToolsRunning = false;
        try
        {
            var processes = Process.GetProcessesByName("HiToolsDelivery");
            hiToolsRunning = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Conflict detection is advisory. Discovery still runs with its own strict timeout.
        }

        var udpListenerPresent = false;
        try
        {
            udpListenerPresent = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .Any(endpoint => endpoint.Port == 37020);
        }
        catch (NetworkInformationException)
        {
            // A notice based on the process check remains useful if listener enumeration fails.
        }

        return new HikvisionSadpConflictState(hiToolsRunning, udpListenerPresent);
    }
}

internal sealed record HikvisionSadpXmlParseResult(
    HikvisionSadpOutputStatus Status,
    IReadOnlyList<IpcDiscoveryDevice> Devices);

internal static class HikvisionSadpXmlParser
{
    internal static HikvisionSadpXmlParseResult Parse(
        TextReader output,
        int maximumDevices)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maximumDevices is < 1 or > 256)
        {
            return Invalid();
        }

        var boundedOutput = new BoundedSadpXmlPayloadReader(
            output,
            HikvisionSadpDiscoveryService.MaximumDiscoveryOutputCharacters);
        HikvisionSadpXmlParseResult result;
        try
        {
            result = ParseCore(boundedOutput, maximumDevices);
        }
        catch (SadpOutputLimitExceededException)
        {
            return TooLarge();
        }
        catch (Exception exception) when (
            exception is XmlException or IOException or FormatException or OverflowException or DecoderFallbackException)
        {
            result = Invalid();
        }

        // XmlReader can stop at the first malformed node. Drain through the same bounded
        // reader so even rejected output cannot bypass the hard stdout limit or block the child.
        try
        {
            var discard = new char[4096];
            while (boundedOutput.Read(discard, 0, discard.Length) > 0)
            {
            }
        }
        catch (SadpOutputLimitExceededException)
        {
            return TooLarge();
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException)
        {
            return Invalid();
        }

        return result;
    }

    private static HikvisionSadpXmlParseResult ParseCore(
        TextReader output,
        int maximumDevices)
    {
        using var reader = XmlReader.Create(output, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = HikvisionSadpDiscoveryService.MaximumDiscoveryOutputCharacters
        });
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "SADPDeviceList" ||
            !string.IsNullOrEmpty(reader.NamespaceURI))
        {
            return Invalid();
        }

        var candidates = new List<IpcDiscoveryDevice>(Math.Min(maximumDevices, 64));
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return reader.MoveToContent() == XmlNodeType.None
                ? Valid([])
                : Invalid();
        }

        reader.ReadStartElement("SADPDeviceList");
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            if (reader.LocalName != "ProbeMatch" || !string.IsNullOrEmpty(reader.NamespaceURI))
            {
                return Invalid();
            }
            if (!TryReadDevice(reader, out var parsed))
            {
                return Invalid();
            }
            if (candidates.Count >= maximumDevices)
            {
                continue;
            }

            var model = SafeText(parsed.DeviceType, 128);
            var remoteAddress = parsed.IPv4Address.Trim();
            if (!IPAddress.TryParse(remoteAddress, out var parsedAddress) ||
                parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }
            remoteAddress = parsedAddress.ToString();
            if (!seenAddresses.Add(remoteAddress))
            {
                continue;
            }

            var httpPort = parsed.HttpPort is >= 1 and <= 65535 ? parsed.HttpPort : 80;
            var channelCount = Math.Clamp(parsed.AnalogChannelCount + parsed.DigitalChannelCount, 0, 1024);
            var displayName = string.IsNullOrWhiteSpace(model) ? "海康设备" : model;
            if (channelCount > 1)
            {
                displayName = $"{displayName} · {channelCount} 路";
            }
            var serviceAddress = new UriBuilder(Uri.UriSchemeHttp, remoteAddress, httpPort).Uri.AbsoluteUri;
            candidates.Add(new IpcDiscoveryDevice(
                string.Empty,
                displayName,
                "Hikvision",
                model,
                remoteAddress,
                [serviceAddress],
                [])
            {
                Source = IpcDiscoverySource.HikvisionSadp
            });
        }

        if (reader.NodeType != XmlNodeType.EndElement || reader.LocalName != "SADPDeviceList")
        {
            return Invalid();
        }
        reader.ReadEndElement();
        if (reader.MoveToContent() != XmlNodeType.None)
        {
            return Invalid();
        }

        return Valid(IpcDiscoveryResultNormalizer.Normalize(candidates, maximumDevices));
    }

    private static bool TryReadDevice(XmlReader reader, out ParsedSadpDevice device)
    {
        device = new ParsedSadpDevice();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return false;
        }

        var seenAllowedFields = new HashSet<string>(StringComparer.Ordinal);
        reader.ReadStartElement("ProbeMatch");
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            var field = reader.LocalName;
            switch (field)
            {
                case "DeviceType":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.DeviceType = reader.ReadElementContentAsString();
                    break;
                case "IPv4Address":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.IPv4Address = reader.ReadElementContentAsString();
                    break;
                case "CommandPort":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.CommandPort = ReadBoundedInteger(reader, 0, 65535);
                    break;
                case "HttpPort":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.HttpPort = ReadBoundedInteger(reader, 0, 65535);
                    break;
                case "AnalogChannelNum":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.AnalogChannelCount = ReadBoundedInteger(reader, 0, 1024);
                    break;
                case "DigitalChannelNum":
                    if (!seenAllowedFields.Add(field)) return false;
                    device.DigitalChannelCount = ReadBoundedInteger(reader, 0, 1024);
                    break;
                default:
                    // DeviceSN, MAC and all other vendor fields are skipped without being
                    // materialized as strings, and never reach notices, logs or domain data.
                    reader.Skip();
                    break;
            }
        }
        if (reader.NodeType != XmlNodeType.EndElement || reader.LocalName != "ProbeMatch")
        {
            return false;
        }
        reader.ReadEndElement();
        return !string.IsNullOrWhiteSpace(device.IPv4Address);
    }

    private static int ReadBoundedInteger(XmlReader reader, int minimum, int maximum)
    {
        var value = reader.ReadElementContentAsString();
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
               parsed >= minimum && parsed <= maximum
            ? parsed
            : 0;
    }

    private static HikvisionSadpXmlParseResult Valid(IReadOnlyList<IpcDiscoveryDevice> devices) =>
        new(HikvisionSadpOutputStatus.Valid, devices);

    private static HikvisionSadpXmlParseResult Invalid() =>
        new(HikvisionSadpOutputStatus.Invalid, []);

    private static HikvisionSadpXmlParseResult TooLarge() =>
        new(HikvisionSadpOutputStatus.TooLarge, []);

    private static string SafeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength && !trimmed.Any(char.IsControl)
            ? trimmed
            : string.Empty;
    }

    private sealed class ParsedSadpDevice
    {
        internal string DeviceType { get; set; } = string.Empty;
        internal string IPv4Address { get; set; } = string.Empty;
        internal int CommandPort { get; set; }
        internal int HttpPort { get; set; }
        internal int AnalogChannelCount { get; set; }
        internal int DigitalChannelCount { get; set; }
    }
}

/// <summary>
/// Locates the strict XML marker at a line boundary and then exposes the original stream
/// directly. Only the fixed marker is buffered; arbitrary vendor fields are never combined
/// into a complete string. Every consumed character, including the discarded preamble, is
/// counted against the same hard output limit.
/// </summary>
internal sealed class BoundedSadpXmlPayloadReader(
    TextReader source,
    int maximumCharacters) : TextReader
{
    private const string XmlDeclarationMarker = "<?xml";
    private const string RootMarker = "<SADPDeviceList";

    private readonly TextReader _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly int _maximumCharacters = maximumCharacters > 0
        ? maximumCharacters
        : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
    private string _fixedPrefix = string.Empty;
    private int _fixedPrefixOffset;
    private int _charactersRead;
    private bool _prepared;
    private bool _payloadFound;

    public override int Read()
    {
        EnsurePrepared();
        if (!_payloadFound)
        {
            return -1;
        }
        if (_fixedPrefixOffset < _fixedPrefix.Length)
        {
            return _fixedPrefix[_fixedPrefixOffset++];
        }
        return ReadSourceCharacter();
    }

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - index < count)
        {
            throw new ArgumentException("Buffer range is invalid.", nameof(count));
        }

        var copied = 0;
        while (copied < count)
        {
            var value = Read();
            if (value < 0)
            {
                break;
            }
            buffer[index + copied++] = (char)value;
        }
        return copied;
    }

    private void EnsurePrepared()
    {
        if (_prepared)
        {
            return;
        }
        _prepared = true;

        var atLineStart = true;
        while (true)
        {
            var value = ReadSourceCharacter();
            if (value < 0)
            {
                return;
            }

            var current = (char)value;
            if (atLineStart && current == '\uFEFF')
            {
                continue;
            }
            if (atLineStart && current == '<')
            {
                if (TryReadFixedMarker(out var marker, out atLineStart))
                {
                    _fixedPrefix = marker;
                    _payloadFound = true;
                    return;
                }
                continue;
            }
            atLineStart = current is '\r' or '\n';
        }
    }

    private bool TryReadFixedMarker(out string marker, out bool atLineStart)
    {
        marker = string.Empty;
        atLineStart = false;
        var candidate = new char[RootMarker.Length];
        candidate[0] = '<';
        var length = 1;

        while (true)
        {
            var xmlPossible = IsPrefix(candidate, length, XmlDeclarationMarker);
            var rootPossible = IsPrefix(candidate, length, RootMarker);
            if (xmlPossible && length == XmlDeclarationMarker.Length)
            {
                marker = XmlDeclarationMarker;
                return true;
            }
            if (rootPossible && length == RootMarker.Length)
            {
                marker = RootMarker;
                return true;
            }
            if (!xmlPossible && !rootPossible)
            {
                atLineStart = candidate[length - 1] is '\r' or '\n';
                return false;
            }

            var value = ReadSourceCharacter();
            if (value < 0)
            {
                return false;
            }
            candidate[length++] = (char)value;
        }
    }

    private static bool IsPrefix(char[] candidate, int length, string marker)
    {
        if (length > marker.Length)
        {
            return false;
        }
        for (var index = 0; index < length; index++)
        {
            if (candidate[index] != marker[index])
            {
                return false;
            }
        }
        return true;
    }

    private int ReadSourceCharacter()
    {
        var value = _source.Read();
        if (value < 0)
        {
            return -1;
        }
        if (++_charactersRead > _maximumCharacters)
        {
            throw new SadpOutputLimitExceededException();
        }
        return value;
    }
}

internal sealed class SadpOutputLimitExceededException : IOException;
