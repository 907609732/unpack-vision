namespace UnpackVision.Core;

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

/// <summary>
/// Optional, consent-gated product telemetry. Implementations must never block
/// recording and must not include business data.
/// </summary>
public interface IUsageTelemetry
{
    Task TrackAsync(
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default);
}

public sealed class NullEventPublisher : IEventPublisher
{
    public Task PublishAsync(string eventType, ScanRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class NoOpUsageTelemetry : IUsageTelemetry
{
    public Task TrackAsync(
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
