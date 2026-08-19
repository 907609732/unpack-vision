using UnpackVision.Core.Recording;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Serializes storage admission so one order is pinned to exactly one root and concurrent
/// starts cannot observe the same unreserved capacity. Releasing an order only removes its
/// in-memory reservation; this component never deletes, moves, or truncates media.
/// </summary>
public sealed class RecordingStoragePoolAllocator(IStoragePoolMonitor monitor) : IRecordingStorageAllocator
{
    private readonly IStoragePoolMonitor _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StorageAllocation> _allocations = new(StringComparer.Ordinal);

    public async Task<StorageAllocationResult> AllocateAsync(
        StoragePoolOptions options,
        StorageAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var validationMessage = Validate(options, request);
            if (validationMessage is not null)
            {
                return StorageAllocationResult.Failure(
                    StorageAllocationFailureReason.InvalidRequest,
                    validationMessage,
                    new StoragePoolState([]));
            }

            var reservations = BuildReservationTotals();
            var poolState = await _monitor.InspectAsync(
                options,
                new StorageCapacityRequest(
                    request.ProjectedMaximumRecordBytes,
                    request.EstimatedBytesPerHour,
                    reservations),
                cancellationToken);

            if (_allocations.TryGetValue(request.OrderId, out var existingAllocation))
            {
                return StorageAllocationResult.Success(existingAllocation, poolState);
            }

            var target = poolState.Targets.FirstOrDefault(state => state.Health == StorageTargetHealth.Ready)
                ?? poolState.Targets.FirstOrDefault(state => state.Health == StorageTargetHealth.Warning);
            if (target is null)
            {
                return CreateFailure(options, poolState);
            }

            var warningMessage = target.WarningReasons == StorageWarningReason.None
                ? null
                : CreateWarningMessage(target.WarningReasons);
            var allocation = new StorageAllocation(
                request.OrderId,
                target.TargetId,
                target.DisplayName,
                target.RootPath,
                request.ProjectedMaximumRecordBytes,
                target.EffectiveAvailableBytes,
                target.WarningReasons,
                warningMessage);
            _allocations.Add(request.OrderId, allocation);
            return StorageAllocationResult.Success(allocation, poolState);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _allocations.Remove(orderId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyDictionary<string, long> BuildReservationTotals()
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var allocation in _allocations.Values)
        {
            totals.TryGetValue(allocation.TargetId, out var current);
            totals[allocation.TargetId] = AddWithoutOverflow(current, allocation.ReservedBytes);
        }

        return totals;
    }

    private static long AddWithoutOverflow(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static StorageAllocationResult CreateFailure(
        StoragePoolOptions options,
        StoragePoolState poolState)
    {
        var enabledTargets = (options.Targets ?? []).Where(target => target.Enabled).ToArray();
        if (enabledTargets.Length == 0)
        {
            return StorageAllocationResult.Failure(
                StorageAllocationFailureReason.NoTargetsConfigured,
                "No enabled recording storage target is configured.",
                poolState);
        }

        var hasReachableWritableTarget = poolState.Targets.Any(state =>
            state.Health is StorageTargetHealth.InsufficientSpace or
                StorageTargetHealth.Warning or
                StorageTargetHealth.Ready);
        return hasReachableWritableTarget
            ? StorageAllocationResult.Failure(
                StorageAllocationFailureReason.InsufficientSpace,
                "All writable recording targets are below the safe free-space reserve.",
                poolState)
            : StorageAllocationResult.Failure(
                StorageAllocationFailureReason.NoHealthyTargets,
                "No enabled recording target is online, writable, and matched to its configured volume.",
                poolState);
    }

    private static string CreateWarningMessage(StorageWarningReason warningReasons)
    {
        var messages = new List<string>(2);
        if (warningReasons.HasFlag(StorageWarningReason.LowFreeSpacePercentage))
        {
            messages.Add("free space is below the configured percentage warning threshold");
        }
        if (warningReasons.HasFlag(StorageWarningReason.LowEstimatedRecordingTime))
        {
            messages.Add("estimated remaining recording time is below the configured warning threshold");
        }
        return string.Join("; ", messages);
    }

    private static string? Validate(StoragePoolOptions options, StorageAllocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            return "Order id cannot be empty.";
        }
        if (request.ProjectedMaximumRecordBytes <= 0)
        {
            return "Projected maximum recording bytes must be greater than zero.";
        }
        if (request.EstimatedBytesPerHour <= 0)
        {
            return "Estimated bytes per hour must be greater than zero.";
        }

        var targets = options.Targets ?? [];
        var enabledTargets = targets.Where(target => target.Enabled).ToArray();
        if (enabledTargets.Any(target =>
                string.IsNullOrWhiteSpace(target.Id) || string.IsNullOrWhiteSpace(target.RootPath)))
        {
            return "Every enabled recording target requires a stable id and root path.";
        }

        if (enabledTargets
            .GroupBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            return "Enabled recording target ids must be unique.";
        }

        return null;
    }
}
