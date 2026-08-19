using System.Diagnostics;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Discovers ONVIF devices without credentials. It first uses the standard ONVIF network
/// video transmitter type and only falls back to an untyped compatibility probe when the
/// standard pass yields no safe candidates.
/// </summary>
public sealed class OnvifIpcDiscoveryService : IIpcDiscoveryService
{
    private readonly IWsDiscoveryTransport _transport;

    public OnvifIpcDiscoveryService()
        : this(new WsDiscoveryUdpTransport())
    {
    }

    internal OnvifIpcDiscoveryService(IWsDiscoveryTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<IpcDiscoveryScanResult> DiscoverAsync(
        IpcDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeout = request.EffectiveTimeout;
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "IPC 扫描时间必须在 1 到 15 秒之间。");
        }
        if (request.MaximumDevices is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "IPC 扫描设备上限必须在 1 到 256 之间。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var standardBudget = TimeSpan.FromTicks(timeout.Ticks / 2);
        var standard = await _transport.ProbeAsync(
            WsDiscoveryProbeKind.NetworkVideoTransmitter,
            standardBudget,
            request.MaximumDevices,
            cancellationToken).ConfigureAwait(false);
        var devices = IpcDiscoveryResultNormalizer.Normalize(
            standard.Devices,
            request.MaximumDevices);
        var notices = new List<string>(standard.Notices);

        if (devices.Count == 0)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                notices.Add("标准 ONVIF 探测未返回设备，已自动使用兼容探测。");
                var compatibility = await _transport.ProbeAsync(
                    WsDiscoveryProbeKind.Untyped,
                    remaining,
                    request.MaximumDevices,
                    cancellationToken).ConfigureAwait(false);
                devices = IpcDiscoveryResultNormalizer.Normalize(
                    compatibility.Devices,
                    request.MaximumDevices);
                notices.AddRange(compatibility.Notices);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new IpcDiscoveryScanResult(
            devices,
            notices
                .Where(notice => !string.IsNullOrWhiteSpace(notice))
                .Distinct(StringComparer.Ordinal)
                .Take(16)
                .ToArray());
    }
}
