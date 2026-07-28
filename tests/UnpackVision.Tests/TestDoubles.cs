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
    public int SnapshotCount { get; private set; }
    public IReadOnlyList<RecordTagAssignment> OverlayTags { get; private set; } = [];

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
    public Task UpdateIssueOverlayAsync(Guid recordId, IReadOnlyList<RecordTagAssignment> activeTags, CancellationToken cancellationToken = default)
    {
        OverlayTags = activeTags.ToArray();
        return Task.CompletedTask;
    }
    public Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotCount++;
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, $"snapshot-{SnapshotCount}.jpg");
        File.WriteAllBytes(path, [4, 5, 6]);
        return Task.FromResult(path);
    }
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
            record.TrackingNo == trackingNo && record.State is RecordingState.Completed or RecordingState.Collected or RecordingState.Imported));
    public Task<ScanRecord?> FindByVideoPathAsync(string videoPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.FirstOrDefault(record => string.Equals(record.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase)));
    public Task<IReadOnlyList<ScanRecord>> QueryAsync(string? trackingNo = null, int limit = 200, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScanRecord>>(Records.Take(limit).ToList());
    public Task<IReadOnlyList<ScanRecord>> QueryPageAsync(string? trackingNo, int offset, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScanRecord>>(Records
            .Where(record => string.IsNullOrWhiteSpace(trackingNo) || record.TrackingNo.Contains(trackingNo, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.ScannedAt)
            .Skip(Math.Max(0, offset))
            .Take(limit)
            .ToList());
    public Task<int> DeleteManyAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        var ids = recordIds.ToHashSet();
        Deliveries.RemoveAll(delivery => ids.Contains(delivery.RecordId));
        return Task.FromResult(Records.RemoveAll(record => ids.Contains(record.Id)));
    }
    public Task<RecordTagAssignment> AddTagAsync(Guid recordId, IssueTagDefinition tag, DateTimeOffset taggedAt, string source = "scanner", CancellationToken cancellationToken = default)
    {
        var record = Records.Single(item => item.Id == recordId);
        var existing = record.Tags.FirstOrDefault(item => item.IsActive && string.Equals(item.TagId, tag.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }
        var assignment = new RecordTagAssignment
        {
            RecordId = recordId,
            TagId = tag.Id,
            TagName = tag.Name,
            ColorHex = tag.ColorHex,
            TaggedAt = taggedAt,
            Source = source
        };
        record.Tags = [.. record.Tags, assignment];
        return Task.FromResult(assignment);
    }
    public Task<RecordTagAssignment?> UndoLastTagAsync(Guid recordId, DateTimeOffset removedAt, CancellationToken cancellationToken = default)
    {
        var record = Records.Single(item => item.Id == recordId);
        var assignment = record.Tags.Where(item => item.IsActive).OrderByDescending(item => item.TaggedAt).FirstOrDefault();
        if (assignment is not null)
        {
            assignment.RemovedAt = removedAt;
        }
        return Task.FromResult(assignment);
    }
    public Task<RecordTagAssignment?> RemoveTagAsync(Guid recordId, Guid assignmentId, DateTimeOffset removedAt, CancellationToken cancellationToken = default)
    {
        var record = Records.Single(item => item.Id == recordId);
        var assignment = record.Tags.FirstOrDefault(item => item.Id == assignmentId && item.IsActive);
        if (assignment is not null)
        {
            assignment.RemovedAt = removedAt;
        }
        return Task.FromResult(assignment);
    }
    public Task UpdateNoteAsync(Guid recordId, string note, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        var record = Records.Single(item => item.Id == recordId);
        record.Note = note.Trim();
        record.NoteUpdatedAt = updatedAt;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RecordTagAssignment>> GetTagsAsync(Guid recordId, bool includeRemoved = false, CancellationToken cancellationToken = default)
    {
        var tags = Records.Single(item => item.Id == recordId).Tags.Where(item => includeRemoved || item.IsActive).ToArray();
        return Task.FromResult<IReadOnlyList<RecordTagAssignment>>(tags);
    }
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
