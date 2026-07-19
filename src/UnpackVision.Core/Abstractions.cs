namespace UnpackVision.Core;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public interface IRecordingBackend : IAsyncDisposable
{
    Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken);

    Task<RecordingCompletion> StopAsync(
        RecordingSession session,
        CancellationToken cancellationToken);

    Task AbortAsync(RecordingSession session, CancellationToken cancellationToken);
}

public interface IScanRecordRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AddAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task AddImportedAsync(ScanRecord record, string? connectorId, CancellationToken cancellationToken = default);
    Task UpdateAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task CompleteAndEnqueueAsync(ScanRecord record, string connectorId, CancellationToken cancellationToken = default);
    Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ScanRecord?> FindFirstCompletedAsync(string trackingNo, CancellationToken cancellationToken = default);
    Task<ScanRecord?> FindByVideoPathAsync(string videoPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanRecord>> QueryAsync(
        string? trackingNo = null,
        int limit = 200,
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

public sealed record ConnectorHealth(bool Healthy, string Message);
public sealed record SyncPushResult(string? ExternalId, string Message);

public interface ISyncConnector
{
    string Id { get; }
    IReadOnlyList<string> ValidateConfiguration();
    Task<ConnectorHealth> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<SyncPushResult> PushRecordAsync(ScanRecord record, CancellationToken cancellationToken = default);
    Task<ConnectorHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

public interface IEventPublisher
{
    Task PublishAsync(string eventType, ScanRecord record, CancellationToken cancellationToken = default);
}

public sealed class NullEventPublisher : IEventPublisher
{
    public Task PublishAsync(string eventType, ScanRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
