namespace UnpackVision.Core.Recording;

/// <summary>
/// Configures the ordered set of recording roots that may receive a complete order.
/// Targets are intentionally not capped so a future host can expose more bays without
/// changing the recording contract.
/// </summary>
public sealed class StoragePoolOptions
{
    public const long Gibibyte = 1024L * 1024 * 1024;
    public const string LegacyTargetId = "legacy-recording-root";

    public List<StorageTarget> Targets { get; set; } = [];
    public double WarningFreeSpacePercent { get; set; } = 15;
    public TimeSpan WarningRemainingRecordingTime { get; set; } = TimeSpan.FromHours(2);
    public long MinimumSafeReserveBytes { get; set; } = 10 * Gibibyte;
    public long SafeReservePaddingBytes { get; set; } = 5 * Gibibyte;

    /// <summary>
    /// Provides the compatibility bridge used by settings migration: an existing single
    /// recording root becomes the first target without moving any media.
    /// </summary>
    public static StoragePoolOptions FromLegacyRecordingRoot(string recordingRoot)
    {
        if (string.IsNullOrWhiteSpace(recordingRoot))
        {
            throw new ArgumentException("Recording root cannot be empty.", nameof(recordingRoot));
        }

        return new StoragePoolOptions
        {
            Targets =
            [
                new StorageTarget
                {
                    Id = LegacyTargetId,
                    DisplayName = "Default recording drive",
                    RootPath = recordingRoot.Trim(),
                    Priority = 0,
                    Enabled = true
                }
            ]
        };
    }

    public long CalculateSafeReserveBytes(long projectedMaximumRecordBytes)
    {
        if (projectedMaximumRecordBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projectedMaximumRecordBytes),
                "Projected recording bytes cannot be negative.");
        }

        var padding = Math.Max(0, SafeReservePaddingBytes);
        var projectedWithPadding = projectedMaximumRecordBytes > long.MaxValue - padding
            ? long.MaxValue
            : projectedMaximumRecordBytes + padding;
        return Math.Max(Math.Max(0, MinimumSafeReserveBytes), projectedWithPadding);
    }
}

public sealed class StorageTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Recording drive";
    public string RootPath { get; set; } = string.Empty;
    public string VolumeId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}

public enum StorageTargetHealth
{
    Disabled,
    Offline,
    ReadOnly,
    VolumeMismatch,
    InsufficientSpace,
    Warning,
    Ready
}

[Flags]
public enum StorageWarningReason
{
    None = 0,
    LowFreeSpacePercentage = 1,
    LowEstimatedRecordingTime = 2
}

/// <summary>
/// Raw filesystem facts supplied by an Infrastructure probe. No operating-system
/// filesystem type crosses this boundary.
/// </summary>
public sealed record StorageDiskProbeResult(
    bool IsOnline,
    bool IsWritable,
    string VolumeId,
    long TotalBytes,
    long AvailableBytes,
    string? Message = null);

public sealed record StorageCapacityRequest(
    long ProjectedMaximumRecordBytes,
    long EstimatedBytesPerHour,
    IReadOnlyDictionary<string, long>? ReservedBytesByTarget = null);

public sealed record StorageTargetState(
    string TargetId,
    string DisplayName,
    string RootPath,
    string VolumeId,
    int Priority,
    StorageTargetHealth Health,
    long TotalBytes,
    long AvailableBytes,
    long ReservedBytes,
    long EffectiveAvailableBytes,
    long RequiredSafeReserveBytes,
    double FreeSpacePercent,
    TimeSpan? EstimatedRecordingTime,
    StorageWarningReason WarningReasons,
    string? Message)
{
    public bool CanAllocate => Health is StorageTargetHealth.Ready or StorageTargetHealth.Warning;
}

public sealed record StoragePoolState(IReadOnlyList<StorageTargetState> Targets);

public sealed record StorageAllocationRequest(
    string OrderId,
    long ProjectedMaximumRecordBytes,
    long EstimatedBytesPerHour);

/// <summary>
/// Pins every media asset for one order to one recording root. The reservation is an
/// admission-control estimate, not a request to preallocate or move files.
/// </summary>
public sealed record StorageAllocation(
    string OrderId,
    string TargetId,
    string TargetDisplayName,
    string RootPath,
    long ReservedBytes,
    long AvailableBytesAtAllocation,
    StorageWarningReason WarningReasons,
    string? WarningMessage);

public enum StorageAllocationFailureReason
{
    None,
    InvalidRequest,
    NoTargetsConfigured,
    NoHealthyTargets,
    InsufficientSpace
}

public sealed record StorageAllocationResult(
    bool Succeeded,
    StorageAllocation? Allocation,
    StorageAllocationFailureReason FailureReason,
    string Message,
    StoragePoolState PoolState)
{
    public static StorageAllocationResult Success(StorageAllocation allocation, StoragePoolState poolState) =>
        new(true, allocation, StorageAllocationFailureReason.None, string.Empty, poolState);

    public static StorageAllocationResult Failure(
        StorageAllocationFailureReason reason,
        string message,
        StoragePoolState poolState) =>
        new(false, null, reason, message, poolState);
}

public interface IStorageDiskProbe
{
    ValueTask<StorageDiskProbeResult> ProbeAsync(
        StorageTarget target,
        CancellationToken cancellationToken = default);
}

public interface IStoragePoolMonitor
{
    Task<StoragePoolState> InspectAsync(
        StoragePoolOptions options,
        StorageCapacityRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRecordingStorageAllocator
{
    Task<StorageAllocationResult> AllocateAsync(
        StoragePoolOptions options,
        StorageAllocationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(string orderId, CancellationToken cancellationToken = default);
}
