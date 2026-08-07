namespace UnpackVision.Core;

/// <summary>
/// Durable record boundary shared by recording, history, issue, sync, and
/// recovery workflows. Implementations must keep merge and enqueue operations
/// idempotent because startup recovery can replay them.
/// </summary>
public interface IScanRecordRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AddAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task AddImportedAsync(ScanRecord record, string? connectorId, CancellationToken cancellationToken = default);
    Task UpdateAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task<bool> MergeRecoveredAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task CompleteAndEnqueueAsync(ScanRecord record, string connectorId, CancellationToken cancellationToken = default);
    Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ScanRecord?> FindFirstCompletedAsync(string trackingNo, CancellationToken cancellationToken = default);
    Task<ScanRecord?> FindByVideoPathAsync(string videoPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanRecord>> QueryAsync(
        string? trackingNo = null,
        int limit = 200,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanRecord>> QueryPageAsync(
        string? trackingNo,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);
    Task<int> DeleteManyAsync(
        IReadOnlyCollection<Guid> recordIds,
        CancellationToken cancellationToken = default);
    Task<RecordTagAssignment> AddTagAsync(
        Guid recordId,
        IssueTagDefinition tag,
        DateTimeOffset taggedAt,
        string source = "scanner",
        CancellationToken cancellationToken = default);
    Task<RecordTagAssignment?> UndoLastTagAsync(
        Guid recordId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken = default);
    Task<RecordTagAssignment?> RemoveTagAsync(
        Guid recordId,
        Guid assignmentId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken = default);
    Task UpdateNoteAsync(
        Guid recordId,
        string note,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordTagAssignment>> GetTagsAsync(
        Guid recordId,
        bool includeRemoved = false,
        CancellationToken cancellationToken = default);
    Task EnqueueDeliveryAsync(
        Guid recordId,
        string connectorId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncDelivery>> GetDueDeliveriesAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<SyncDelivery?> GetDeliveryAsync(
        Guid recordId,
        string connectorId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, SyncDelivery>> GetLatestDeliveriesAsync(
        IReadOnlyCollection<Guid> recordIds,
        string connectorId,
        CancellationToken cancellationToken = default);
    Task<bool> TryClaimDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task CompleteDeliveryAsync(Guid deliveryId, string? externalId, CancellationToken cancellationToken = default);
    Task FailDeliveryAsync(
        Guid deliveryId,
        string error,
        DateTimeOffset nextRetryAt,
        CancellationToken cancellationToken = default);
    Task RetryConnectorAsync(string connectorId, CancellationToken cancellationToken = default);
    Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default);
    Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default);
}
