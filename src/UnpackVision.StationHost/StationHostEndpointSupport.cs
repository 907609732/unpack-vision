using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnpackVision.Core;

namespace UnpackVision.StationHost;

/// <summary>
/// Shared host-boundary helpers. Keeping these outside Program.cs makes the
/// startup pipeline and endpoint map readable without moving security policy
/// into Infrastructure.
/// </summary>
internal static class StationHostEndpointSupport
{
    internal static T BindOptions<T>(IConfiguration configuration, string sectionName) where T : new()
    {
        var value = configuration.GetSection(sectionName).Get<T>() ?? new T();
        foreach (var property in typeof(T).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            if (property.GetValue(value) is string text)
            {
                property.SetValue(value, Environment.ExpandEnvironmentVariables(text));
            }
        }
        return value;
    }

    internal static IPAddress[] GetPrivateIpv4Addresses()
    {
        var privateInterfaceIndexes = GetPrivateNetworkInterfaceIndexes();
        if (privateInterfaceIndexes.Count == 0)
        {
            return [];
        }
        return [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(item =>
            {
                try
                {
                    return privateInterfaceIndexes.Contains(
                        item.GetIPProperties().GetIPv4Properties()?.Index ?? -1);
                }
                catch (NetworkInformationException)
                {
                    return false;
                }
            })
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address))
            .Distinct()
            .OrderBy(address => address.ToString())];
    }

    private static HashSet<int> GetPrivateNetworkInterfaceIndexes()
    {
        try
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell))
            {
                return [];
            }
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments =
                    "-NoProfile -NonInteractive -Command " +
                    "\"Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Private' | " +
                    "Select-Object -ExpandProperty InterfaceIndex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null || !process.WaitForExit(3000))
            {
                try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return [];
            }
            return process.StandardOutput.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var index) ? index : -1)
                .Where(index => index >= 0)
                .ToHashSet();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    internal static string GetAdvertisedHost(StationHostOptions options)
    {
        if (!Uri.TryCreate(options.AdvertisedAddress, UriKind.Absolute, out var address) ||
            string.IsNullOrWhiteSpace(address.Host))
        {
            throw new InvalidOperationException("工位没有可用的局域网安全地址");
        }
        return address.Host;
    }

    internal static bool IsPathUnderRoot(string path, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    internal static (string DeviceId, string AccessToken)? ReadDeviceAuthorization(HttpRequest request)
    {
        var deviceId = request.Headers["X-UnpackVision-Device"].ToString().Trim();
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(deviceId) || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var token = authorization[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : (deviceId, token);
    }

    internal static async Task<bool> AuthorizeAsync(
        HttpContext context,
        IPairedDeviceRegistry devices,
        string scope,
        CancellationToken cancellationToken)
    {
        if (IsLoopback(context))
        {
            return true;
        }
        var authorization = ReadDeviceAuthorization(context.Request);
        return authorization is not null && await devices.AuthenticateAsync(
            authorization.Value.DeviceId,
            authorization.Value.AccessToken,
            scope,
            cancellationToken) is not null;
    }

    internal static async Task<bool> RemovePairedDeviceAsync(
        string deviceId,
        IPairedDeviceRegistry devices,
        IMediaRelayManager mediaRelay,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!await devices.RevokeAsync(deviceId, clock.Now, cancellationToken))
        {
            return false;
        }
        await mediaRelay.DisconnectDeviceAsync(deviceId, cancellationToken);
        return await devices.DeleteAsync(deviceId, cancellationToken);
    }

    internal static StationRecordView ToStationRecordView(ScanRecord record)
    {
        var hasVideo = record.VideoPath is { Length: > 0 } && File.Exists(record.VideoPath);
        long? videoBytes = hasVideo ? new FileInfo(record.VideoPath!).Length : null;
        double? duration = record.RecordingStartedAt is { } started && record.RecordingEndedAt is { } ended
            ? Math.Max(0, (ended - started).TotalSeconds)
            : null;
        return new StationRecordView(
            record.Id,
            record.TrackingNo,
            record.Workflow,
            record.State,
            record.ScannedAt,
            record.RecordingStartedAt,
            record.RecordingEndedAt,
            duration,
            record.Note,
            record.Tags,
            record.CameraId,
            record.StationId,
            record.DuplicateOf,
            record.PlatformMatchStatus,
            record.FailureReason,
            hasVideo,
            videoBytes,
            record.Snapshots.Any(File.Exists),
            record.UpdatedAt);
    }

    internal static IReadOnlyList<StationRecordEvent> BuildRecordEvents(
        ScanRecord record,
        IReadOnlyList<RecordTagAssignment> tags)
    {
        var events = new List<StationRecordEvent>
        {
            new("record.scanned", record.ScannedAt, $"扫描单号 {record.TrackingNo}")
        };
        if (record.RecordingStartedAt is { } started)
        {
            events.Add(new("record.started", started, "开始录像"));
        }
        if (record.RecordingEndedAt is { } ended)
        {
            events.Add(new("record.completed", ended, record.State == RecordingState.Failed ? "录像失败" : "录像结束"));
        }
        foreach (var tag in tags)
        {
            events.Add(new("record.tagged", tag.TaggedAt, $"标记异常：{tag.TagName}", tag.TagId, tag.Id));
            if (tag.RemovedAt is { } removedAt)
            {
                events.Add(new("record.tag_removed", removedAt, $"撤销异常：{tag.TagName}", tag.TagId, tag.Id));
            }
        }
        if (record.NoteUpdatedAt is { } noteUpdatedAt)
        {
            events.Add(new("record.note_updated", noteUpdatedAt, "备注已更新"));
        }
        if (!string.IsNullOrWhiteSpace(record.FailureReason))
        {
            events.Add(new("record.failed", record.UpdatedAt, record.FailureReason));
        }
        return events.OrderBy(item => item.At).ToArray();
    }

    internal static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }
        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string EncodeCursor(int offset) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    internal static string GetVideoContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        _ => "video/mp4"
    };

    internal static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
