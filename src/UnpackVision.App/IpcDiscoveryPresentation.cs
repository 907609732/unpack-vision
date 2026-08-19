using System.Globalization;
using System.Net;
using System.Text;
using UnpackVision.Core;

namespace UnpackVision.App;

/// <summary>
/// Converts discovery transport details into stable, user-facing labels. The discovery adapters
/// remain responsible for network access; this type only explains partial results and keeps a
/// service-discovery address separate from a video-stream address.
/// </summary>
internal static class IpcDiscoveryPresentation
{
    internal static string GetSourceLabel(IpcDiscoverySource source)
    {
        return source switch
        {
            IpcDiscoverySource.OnvifWsDiscovery => "ONVIF 标准",
            IpcDiscoverySource.CompatibilityProbe => "兼容探测",
            IpcDiscoverySource.HikvisionSadp => "海康 SADP",
            _ => "局域网发现"
        };
    }

    internal static string GetSourceSummary(IEnumerable<IpcDiscoveryDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var labels = devices
            .SelectMany(device => device.Sources.Append(device.Source))
            .Where(Enum.IsDefined)
            .Distinct()
            .OrderBy(source => source)
            .Select(GetSourceLabel)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return labels.Length == 0 ? "未返回设备" : string.Join("、", labels);
    }

    internal static IReadOnlyList<string> PrepareNotices(
        IEnumerable<string>? notices,
        IEnumerable<IpcDiscoveryDevice>? devices = null)
    {
        if (notices is null)
        {
            return [];
        }

        var hasSadpResult = devices?.Any(device =>
            device.Source == IpcDiscoverySource.HikvisionSadp ||
            device.Sources.Contains(IpcDiscoverySource.HikvisionSadp)) == true;
        var result = new List<string>(capacity: 12);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawNotice in notices)
        {
            var normalized = NormalizeNotice(rawNotice);
            if (normalized.Length == 0)
            {
                continue;
            }

            var friendly = MakeNoticeFriendly(normalized, hasSadpResult);
            if (friendly is null)
            {
                continue;
            }

            var displayNotice = NormalizeNotice(friendly);
            if (displayNotice.Length > 0 && seen.Add(displayNotice))
            {
                result.Add(displayNotice);
                if (result.Count == 12)
                {
                    break;
                }
            }
        }
        return result;
    }

    internal static bool IsHikvisionCandidate(IpcDiscoveryDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Source == IpcDiscoverySource.HikvisionSadp ||
            device.Sources.Contains(IpcDiscoverySource.HikvisionSadp))
        {
            return true;
        }

        return new[] { device.DisplayName, device.Manufacturer, device.Model }
            .Concat(device.Scopes)
            .Any(value => value.Contains("hikvision", StringComparison.OrdinalIgnoreCase) ||
                          value.Contains("海康", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryGetHikvisionRtspHost(IpcDiscoveryDevice device, out string host)
    {
        ArgumentNullException.ThrowIfNull(device);
        host = string.Empty;
        if (!IsHikvisionCandidate(device) ||
            !IPAddress.TryParse(device.RemoteAddress, out var address))
        {
            return false;
        }

        // ONVIF XAddr entries usually end in device_service and are HTTP service endpoints,
        // never RTSP streams. Only the normalized remote IP is used to form a Hikvision candidate.
        host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return true;
    }

    private static string? MakeNoticeFriendly(string notice, bool hasSadpResult)
    {
        var mentionsSadp = notice.Contains("SADP", StringComparison.OrdinalIgnoreCase) ||
                           notice.Contains("37020", StringComparison.OrdinalIgnoreCase);
        var describesContention = notice.Contains("占用", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("端口冲突", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("正在使用", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("绑定失败", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("HiToolsDelivery", StringComparison.OrdinalIgnoreCase) ||
                                  notice.Contains("海康交付助手", StringComparison.OrdinalIgnoreCase);
        if (mentionsSadp && describesContention)
        {
            // UDP listener ownership is advisory. If the helper returned a safe SADP candidate,
            // reporting the source as unavailable would contradict the observed scan result.
            if (hasSadpResult)
            {
                return null;
            }
            return "海康 SADP 扫描暂不可用：UDP 37020 可能正被海康交付助手占用。ONVIF 标准和兼容探测结果仍会保留；关闭交付助手后可重新扫描。";
        }
        return notice;
    }

    private static string NormalizeNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(notice.Length);
        var pendingSpace = false;
        foreach (var character in notice.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }
}
