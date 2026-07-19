using UnpackVision.Core;

namespace UnpackVision.Tests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

internal sealed class FakeRecordingBackend(string outputRoot) : IRecordingBackend
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool FailStart { get; set; }

    public Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        StartCount++;
        if (FailStart)
        {
            throw new InvalidOperationException("camera busy");
        }
        Directory.CreateDirectory(outputRoot);
        var temporary = Path.Combine(outputRoot, $"{recordId:N}.partial.mp4");
        File.WriteAllBytes(temporary, [1, 2, 3]);
        return Task.FromResult(new RecordingSession(recordId, trackingNo, workflow, scannedAt, temporary));
    }

    public Task<RecordingCompletion> StopAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        StopCount++;
        var output = Path.Combine(outputRoot, $"{session.TrackingNo}_20260719080000_20260719080100.mp4");
        File.Move(session.TemporaryPath, output);
        return Task.FromResult(new RecordingCompletion(output, session.StartedAt.AddMinutes(1)));
    }

    public Task AbortAsync(RecordingSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemoryRepository : IScanRecordRepository
{
    public List<ScanRecord> Records { get; } = [];
    public List<SyncDelivery> Deliveries { get; } = [];
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AddAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }
    public Task AddImportedAsync(ScanRecord record, string? connectorId, CancellationToken cancellationToken = default)
    {
        Records.Add(record);
        if (connectorId is not null)
        {
            Deliveries.Add(new SyncDelivery { RecordId = record.Id, ConnectorId = connectorId });
        }
        return Task.CompletedTask;
    }
    public Task UpdateAsync(ScanRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CompleteAndEnqueueAsync(ScanRecord record, string connectorId, CancellationToken cancellationToken = default)
    {
        Deliveries.Add(new SyncDelivery { RecordId = record.Id, ConnectorId = connectorId });
        return Task.CompletedTask;
    }
    public Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.FirstOrDefault(record => record.Id == id));
    public Task<ScanRecord?> FindFirstCompletedAsync(string trackingNo, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.FirstOrDefault(record =>
            record.TrackingNo == trackingNo && record.State is RecordingState.Completed or RecordingState.Imported));
    public Task<ScanRecord?> FindByVideoPathAsync(string videoPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.FirstOrDefault(record => string.Equals(record.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase)));
    public Task<IReadOnlyList<ScanRecord>> QueryAsync(string? trackingNo = null, int limit = 200, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScanRecord>>(Records.Take(limit).ToList());
    public Task EnqueueDeliveryAsync(Guid recordId, string connectorId, CancellationToken cancellationToken = default)
    {
        Deliveries.Add(new SyncDelivery { RecordId = recordId, ConnectorId = connectorId });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<SyncDelivery>> GetDueDeliveriesAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SyncDelivery>>(Deliveries.Take(limit).ToList());
    public Task<SyncDelivery?> GetDeliveryAsync(Guid recordId, string connectorId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Deliveries.LastOrDefault(delivery => delivery.RecordId == recordId && delivery.ConnectorId == connectorId));
    public Task<bool> TryClaimDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task CompleteDeliveryAsync(Guid deliveryId, string? externalId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task FailDeliveryAsync(Guid deliveryId, string error, DateTimeOffset nextRetryAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RetryConnectorAsync(string connectorId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Metadata.GetValueOrDefault(key));
    public Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Metadata[key] = value;
        return Task.CompletedTask;
    }
}
