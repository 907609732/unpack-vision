using UnpackVision.Core.Recording;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Projects operating-system disk facts into recording admission states. Capacity is
/// calculated after active reservations so concurrent orders cannot overcommit a target.
/// </summary>
public sealed class RecordingStoragePoolMonitor(IStorageDiskProbe diskProbe) : IStoragePoolMonitor
{
    private readonly IStorageDiskProbe _diskProbe = diskProbe ?? throw new ArgumentNullException(nameof(diskProbe));

    public async Task<StoragePoolState> InspectAsync(
        StoragePoolOptions options,
        StorageCapacityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var orderedTargets = (options.Targets ?? [])
            .OrderBy(target => target.Priority)
            .ThenBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var states = new List<StorageTargetState>(orderedTargets.Length);
        foreach (var target in orderedTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            states.Add(await InspectTargetAsync(target, options, request, cancellationToken));
        }

        return new StoragePoolState(states);
    }

    private async Task<StorageTargetState> InspectTargetAsync(
        StorageTarget target,
        StoragePoolOptions options,
        StorageCapacityRequest request,
        CancellationToken cancellationToken)
    {
        var safeReserveBytes = options.CalculateSafeReserveBytes(request.ProjectedMaximumRecordBytes);
        if (!target.Enabled)
        {
            return CreateUnavailableState(
                target,
                StorageTargetHealth.Disabled,
                safeReserveBytes,
                "The recording target is disabled.");
        }

        StorageDiskProbeResult probe;
        try
        {
            probe = await _diskProbe.ProbeAsync(target, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateUnavailableState(
                target,
                StorageTargetHealth.Offline,
                safeReserveBytes,
                $"Disk probe failed: {exception.Message}");
        }

        var totalBytes = Math.Max(0, probe.TotalBytes);
        var availableBytes = Math.Max(0, probe.AvailableBytes);
        var reservedBytes = GetReservedBytes(request.ReservedBytesByTarget, target.Id);
        var effectiveAvailableBytes = Math.Max(0, availableBytes - reservedBytes);
        var freeSpacePercent = totalBytes <= 0
            ? 0
            : Math.Min(100, effectiveAvailableBytes * 100d / totalBytes);
        var estimatedRecordingTime = CalculateEstimatedRecordingTime(
            effectiveAvailableBytes,
            request.EstimatedBytesPerHour);
        var warningReasons = CalculateWarningReasons(options, freeSpacePercent, estimatedRecordingTime);

        var health = ResolveHealth(
            target,
            probe,
            effectiveAvailableBytes,
            safeReserveBytes,
            warningReasons);
        return new StorageTargetState(
            target.Id,
            target.DisplayName,
            target.RootPath,
            probe.VolumeId,
            target.Priority,
            health,
            totalBytes,
            availableBytes,
            reservedBytes,
            effectiveAvailableBytes,
            safeReserveBytes,
            freeSpacePercent,
            estimatedRecordingTime,
            warningReasons,
            probe.Message);
    }

    private static StorageTargetHealth ResolveHealth(
        StorageTarget target,
        StorageDiskProbeResult probe,
        long effectiveAvailableBytes,
        long safeReserveBytes,
        StorageWarningReason warningReasons)
    {
        if (!probe.IsOnline)
        {
            return StorageTargetHealth.Offline;
        }

        if (!string.IsNullOrWhiteSpace(target.VolumeId) &&
            !string.Equals(target.VolumeId, probe.VolumeId, StringComparison.OrdinalIgnoreCase))
        {
            return StorageTargetHealth.VolumeMismatch;
        }

        if (!probe.IsWritable)
        {
            return StorageTargetHealth.ReadOnly;
        }

        if (effectiveAvailableBytes < safeReserveBytes)
        {
            return StorageTargetHealth.InsufficientSpace;
        }

        return warningReasons == StorageWarningReason.None
            ? StorageTargetHealth.Ready
            : StorageTargetHealth.Warning;
    }

    private static StorageWarningReason CalculateWarningReasons(
        StoragePoolOptions options,
        double freeSpacePercent,
        TimeSpan? estimatedRecordingTime)
    {
        var reasons = StorageWarningReason.None;
        if (freeSpacePercent < Math.Clamp(options.WarningFreeSpacePercent, 0, 100))
        {
            reasons |= StorageWarningReason.LowFreeSpacePercentage;
        }

        if (estimatedRecordingTime is not null &&
            estimatedRecordingTime < NormalizeWarningDuration(options.WarningRemainingRecordingTime))
        {
            reasons |= StorageWarningReason.LowEstimatedRecordingTime;
        }

        return reasons;
    }

    private static TimeSpan NormalizeWarningDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

    private static TimeSpan? CalculateEstimatedRecordingTime(
        long effectiveAvailableBytes,
        long estimatedBytesPerHour)
    {
        if (estimatedBytesPerHour <= 0)
        {
            return null;
        }

        var hours = effectiveAvailableBytes / (double)estimatedBytesPerHour;
        return hours >= TimeSpan.MaxValue.TotalHours
            ? TimeSpan.MaxValue
            : TimeSpan.FromHours(hours);
    }

    private static long GetReservedBytes(
        IReadOnlyDictionary<string, long>? reservedBytesByTarget,
        string targetId)
    {
        if (reservedBytesByTarget is null)
        {
            return 0;
        }

        foreach (var pair in reservedBytesByTarget)
        {
            if (string.Equals(pair.Key, targetId, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Max(0, pair.Value);
            }
        }

        return 0;
    }

    private static StorageTargetState CreateUnavailableState(
        StorageTarget target,
        StorageTargetHealth health,
        long safeReserveBytes,
        string message) =>
        new(
            target.Id,
            target.DisplayName,
            target.RootPath,
            string.Empty,
            target.Priority,
            health,
            0,
            0,
            0,
            0,
            safeReserveBytes,
            0,
            null,
            StorageWarningReason.None,
            message);

    private static void ValidateRequest(StorageCapacityRequest request)
    {
        if (request.ProjectedMaximumRecordBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Projected recording bytes cannot be negative.");
        }

        if (request.EstimatedBytesPerHour < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Estimated bytes per hour cannot be negative.");
        }
    }
}
