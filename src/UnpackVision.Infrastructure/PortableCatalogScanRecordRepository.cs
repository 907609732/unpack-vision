using System.Collections.Concurrent;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class PortableCatalogScanRecordRepository(
    IScanRecordRepository inner,
    Func<IPortableRecordCatalog> catalogFactory) : IScanRecordRepository
{
    private readonly ConcurrentDictionary<Guid, Guid> _claimedDeliveries = new();

    private IPortableRecordCatalog Catalog() => catalogFactory();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await inner.InitializeAsync(cancellationToken);
        await Catalog().EnsureWorkspaceAsync(cancellationToken: cancellationToken);
        foreach (var record in await inner.QueryAsync(limit: 2000, cancellationToken: cancellationToken))
        {
            await PersistBestEffortAsync(record, cancellationToken);
        }
    }

    public async Task AddAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        await inner.AddAsync(record, cancellationToken);
        await PersistBestEffortAsync(record, cancellationToken);
    }

    public async Task AddImportedAsync(ScanRecord record, string? connectorId, CancellationToken cancellationToken = default)
    {
        await inner.AddImportedAsync(record, connectorId, cancellationToken);
        await PersistBestEffortAsync(record, cancellationToken);
    }

    public async Task UpdateAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        await inner.UpdateAsync(record, cancellationToken);
        await PersistBestEffortAsync(record, cancellationToken);
    }

    public async Task<bool> MergeRecoveredAsync(ScanRecord record, CancellationToken cancellationToken = default)
    {
        var changed = await inner.MergeRecoveredAsync(record, cancellationToken);
        if (changed)
        {
            await PersistBestEffortAsync(await inner.GetAsync(record.Id, cancellationToken) ?? record, cancellationToken);
        }
        return changed;
    }

    public async Task CompleteAndEnqueueAsync(ScanRecord record, string connectorId, CancellationToken cancellationToken = default)
    {
        await inner.CompleteAndEnqueueAsync(record, connectorId, cancellationToken);
        await PersistBestEffortAsync(record, cancellationToken);
    }

    public Task<ScanRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.GetAsync(id, cancellationToken);
    public Task<ScanRecord?> FindFirstCompletedAsync(string trackingNo, CancellationToken cancellationToken = default) =>
        inner.FindFirstCompletedAsync(trackingNo, cancellationToken);
    public Task<ScanRecord?> FindByVideoPathAsync(string videoPath, CancellationToken cancellationToken = default) =>
        inner.FindByVideoPathAsync(videoPath, cancellationToken);
    public Task<IReadOnlyList<ScanRecord>> QueryAsync(string? trackingNo = null, int limit = 200, CancellationToken cancellationToken = default) =>
        inner.QueryAsync(trackingNo, limit, cancellationToken);
    public Task<IReadOnlyList<ScanRecord>> QueryPageAsync(string? trackingNo, int offset, int limit, CancellationToken cancellationToken = default) =>
        inner.QueryPageAsync(trackingNo, offset, limit, cancellationToken);
    public Task<IReadOnlyDictionary<Guid, SyncDelivery>> GetLatestDeliveriesAsync(
        IReadOnlyCollection<Guid> recordIds,
        string connectorId,
        CancellationToken cancellationToken = default) =>
        inner.GetLatestDeliveriesAsync(recordIds, connectorId, cancellationToken);

    public async Task<int> DeleteManyAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        var result = await inner.DeleteManyAsync(recordIds, cancellationToken);
        foreach (var id in recordIds)
        {
            await Catalog().DeleteAsync(id, cancellationToken);
        }
        return result;
    }

    public async Task<RecordTagAssignment> AddTagAsync(Guid recordId, IssueTagDefinition tag, DateTimeOffset taggedAt, string source = "scanner", CancellationToken cancellationToken = default)
    {
        var result = await inner.AddTagAsync(recordId, tag, taggedAt, source, cancellationToken);
        await PersistRecordAsync(recordId, cancellationToken);
        return result;
    }

    public async Task<RecordTagAssignment?> UndoLastTagAsync(Guid recordId, DateTimeOffset removedAt, CancellationToken cancellationToken = default)
    {
        var result = await inner.UndoLastTagAsync(recordId, removedAt, cancellationToken);
        await PersistRecordAsync(recordId, cancellationToken);
        return result;
    }

    public async Task<RecordTagAssignment?> RemoveTagAsync(Guid recordId, Guid assignmentId, DateTimeOffset removedAt, CancellationToken cancellationToken = default)
    {
        var result = await inner.RemoveTagAsync(recordId, assignmentId, removedAt, cancellationToken);
        await PersistRecordAsync(recordId, cancellationToken);
        return result;
    }

    public async Task UpdateNoteAsync(Guid recordId, string note, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await inner.UpdateNoteAsync(recordId, note, updatedAt, cancellationToken);
        await PersistRecordAsync(recordId, cancellationToken);
    }

    public Task<IReadOnlyList<RecordTagAssignment>> GetTagsAsync(Guid recordId, bool includeRemoved = false, CancellationToken cancellationToken = default) =>
        inner.GetTagsAsync(recordId, includeRemoved, cancellationToken);
    public async Task EnqueueDeliveryAsync(Guid recordId, string connectorId, CancellationToken cancellationToken = default)
    {
        await inner.EnqueueDeliveryAsync(recordId, connectorId, cancellationToken);
        await PersistRecordAsync(recordId, cancellationToken);
    }
    public Task<IReadOnlyList<SyncDelivery>> GetDueDeliveriesAsync(int limit, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        inner.GetDueDeliveriesAsync(limit, now, cancellationToken);
    public Task<SyncDelivery?> GetDeliveryAsync(Guid recordId, string connectorId, CancellationToken cancellationToken = default) =>
        inner.GetDeliveryAsync(recordId, connectorId, cancellationToken);
    public async Task<bool> TryClaimDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var due = await inner.GetDueDeliveriesAsync(500, DateTimeOffset.MaxValue, cancellationToken);
        var delivery = due.FirstOrDefault(item => item.Id == deliveryId);
        var claimed = await inner.TryClaimDeliveryAsync(deliveryId, cancellationToken);
        if (claimed && delivery is not null)
        {
            _claimedDeliveries[deliveryId] = delivery.RecordId;
            await PersistRecordAsync(delivery.RecordId, cancellationToken);
        }
        return claimed;
    }

    public async Task CompleteDeliveryAsync(Guid deliveryId, string? externalId, CancellationToken cancellationToken = default)
    {
        await inner.CompleteDeliveryAsync(deliveryId, externalId, cancellationToken);
        if (_claimedDeliveries.TryRemove(deliveryId, out var recordId))
        {
            await PersistRecordAsync(recordId, cancellationToken);
        }
    }

    public async Task FailDeliveryAsync(Guid deliveryId, string error, DateTimeOffset nextRetryAt, CancellationToken cancellationToken = default)
    {
        await inner.FailDeliveryAsync(deliveryId, error, nextRetryAt, cancellationToken);
        if (_claimedDeliveries.TryRemove(deliveryId, out var recordId))
        {
            await PersistRecordAsync(recordId, cancellationToken);
        }
    }

    public async Task RetryConnectorAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        await inner.RetryConnectorAsync(connectorId, cancellationToken);
        foreach (var record in await inner.QueryAsync(limit: 2000, cancellationToken: cancellationToken))
        {
            await PersistBestEffortAsync(record, cancellationToken);
        }
    }
    public Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetMetadataAsync(key, cancellationToken);
    public Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default) =>
        inner.SetMetadataAsync(key, value, cancellationToken);

    private async Task PersistRecordAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await inner.GetAsync(id, cancellationToken);
        if (record is not null)
        {
            await PersistBestEffortAsync(record, cancellationToken);
        }
    }

    private async Task PersistBestEffortAsync(ScanRecord record, CancellationToken cancellationToken)
    {
        try
        {
            record.Tags = await inner.GetTagsAsync(record.Id, true, cancellationToken);
            var delivery = await inner.GetDeliveryAsync(record.Id, "excel", cancellationToken);
            await Catalog().WriteAsync(record, delivery, cancellationToken);
            await inner.SetMetadataAsync($"portable.retry.{record.Id:D}", string.Empty, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await inner.SetMetadataAsync(
                $"portable.retry.{record.Id:D}",
                ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                cancellationToken);
        }
    }
}
