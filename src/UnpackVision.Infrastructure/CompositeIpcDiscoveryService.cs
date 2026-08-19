using System.Net;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Runs standards-based ONVIF and the optional Hikvision SADP compatibility probe in
/// parallel. One failed source becomes a non-sensitive notice and never discards devices
/// returned by the other source.
/// </summary>
public sealed class CompositeIpcDiscoveryService : IIpcDiscoveryService
{
    private readonly IIpcDiscoveryService _onvifDiscovery;
    private readonly IIpcDiscoveryService _hikvisionSadpDiscovery;

    public CompositeIpcDiscoveryService(
        IIpcDiscoveryService onvifDiscovery,
        IIpcDiscoveryService hikvisionSadpDiscovery)
    {
        _onvifDiscovery = onvifDiscovery ?? throw new ArgumentNullException(nameof(onvifDiscovery));
        _hikvisionSadpDiscovery = hikvisionSadpDiscovery ??
            throw new ArgumentNullException(nameof(hikvisionSadpDiscovery));
    }

    public async Task<IpcDiscoveryScanResult> DiscoverAsync(
        IpcDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = request.EffectiveTimeout;
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "IPC 扫描时间必须在 1 到 15 秒之间。");
        }
        if (request.MaximumDevices is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "IPC 扫描设备上限必须在 1 到 256 之间。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(20, timeout.TotalSeconds + 5)));

        var onvifTask = RunSourceAsync(
            _onvifDiscovery,
            request,
            "ONVIF 扫描未能完成；已保留 SADP 扫描结果。",
            totalTimeout.Token,
            cancellationToken);
        var sadpTask = RunSourceAsync(
            _hikvisionSadpDiscovery,
            request,
            "海康 SADP 扫描未能完成；已保留 ONVIF 扫描结果。",
            totalTimeout.Token,
            cancellationToken);

        await Task.WhenAll(onvifTask, sadpTask);
        var scans = new[] { await onvifTask, await sadpTask };
        var devices = MergeDevices(scans.SelectMany(scan => scan.Devices), request.MaximumDevices);
        var notices = scans.SelectMany(scan => scan.Notices)
            .Where(notice => !string.IsNullOrWhiteSpace(notice))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        return new IpcDiscoveryScanResult(devices, notices);
    }

    internal static IReadOnlyList<IpcDiscoveryDevice> MergeDevices(
        IEnumerable<IpcDiscoveryDevice> candidates,
        int maximumDevices)
    {
        var merged = new Dictionary<string, IpcDiscoveryDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Take(maximumDevices * 4))
        {
            if (!IPAddress.TryParse(candidate.RemoteAddress, out var address))
            {
                continue;
            }
            var key = address.ToString();
            if (!merged.TryGetValue(key, out var current))
            {
                merged[key] = candidate;
                continue;
            }

            var sadpReported = current.Source == IpcDiscoverySource.HikvisionSadp ||
                               candidate.Source == IpcDiscoverySource.HikvisionSadp;
            var onvif = IsOnvifFamily(current.Source) ? current :
                IsOnvifFamily(candidate.Source) ? candidate : current;
            var sadp = current.Source == IpcDiscoverySource.HikvisionSadp ? current :
                candidate.Source == IpcDiscoverySource.HikvisionSadp ? candidate : current;

            merged[key] = onvif with
            {
                DisplayName = Prefer(onvif.DisplayName, sadp.DisplayName),
                Manufacturer = Prefer(onvif.Manufacturer, sadp.Manufacturer),
                Model = Prefer(onvif.Model, sadp.Model),
                Addresses = onvif.Addresses.Concat(sadp.Addresses)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToArray(),
                Scopes = onvif.Scopes.Concat(sadp.Scopes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .ToArray(),
                Source = sadpReported ? IpcDiscoverySource.HikvisionSadp : onvif.Source,
                Sources = MergeSources(current, candidate)
            };
        }

        return IpcDiscoveryResultNormalizer.Normalize(merged.Values, maximumDevices);
    }

    private static async Task<IpcDiscoveryScanResult> RunSourceAsync(
        IIpcDiscoveryService source,
        IpcDiscoveryRequest request,
        string failureNotice,
        CancellationToken totalToken,
        CancellationToken callerToken)
    {
        try
        {
            return await source.DiscoverAsync(request, totalToken);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new IpcDiscoveryScanResult([], [failureNotice]);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                         System.Net.Sockets.SocketException)
        {
            return new IpcDiscoveryScanResult([], [failureNotice]);
        }
    }

    private static string Prefer(string primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    private static bool IsOnvifFamily(IpcDiscoverySource source) =>
        source is IpcDiscoverySource.OnvifWsDiscovery or IpcDiscoverySource.CompatibilityProbe;

    private static IReadOnlyList<IpcDiscoverySource> MergeSources(
        IpcDiscoveryDevice first,
        IpcDiscoveryDevice second) =>
        first.Sources
            .Append(first.Source)
            .Concat(second.Sources)
            .Append(second.Source)
            .Where(Enum.IsDefined)
            .Distinct()
            .OrderBy(source => source)
            .ToArray();
}
