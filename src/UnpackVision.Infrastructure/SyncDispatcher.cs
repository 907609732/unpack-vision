using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class SyncDispatcher
{
    private readonly IScanRecordRepository _repository;
    private readonly IReadOnlyDictionary<string, ISyncConnector> _connectors;
    private readonly IClock _clock;

    public SyncDispatcher(
        IScanRecordRepository repository,
        IEnumerable<ISyncConnector> connectors,
        IClock clock)
    {
        _repository = repository;
        _connectors = connectors.ToDictionary(connector => connector.Id, StringComparer.OrdinalIgnoreCase);
        _clock = clock;
    }

    public async Task<int> ProcessDueAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var processed = 0;
        var deliveries = await _repository.GetDueDeliveriesAsync(limit, _clock.Now, cancellationToken);
        foreach (var delivery in deliveries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _repository.TryClaimDeliveryAsync(delivery.Id, cancellationToken))
            {
                continue;
            }

            try
            {
                if (!_connectors.TryGetValue(delivery.ConnectorId, out var connector))
                {
                    throw new InvalidOperationException($"未注册同步连接器：{delivery.ConnectorId}");
                }
                var record = await _repository.GetAsync(delivery.RecordId, cancellationToken)
                    ?? throw new InvalidOperationException($"同步记录不存在：{delivery.RecordId}");
                var result = await connector.PushRecordAsync(record, cancellationToken);
                await _repository.CompleteDeliveryAsync(delivery.Id, result.ExternalId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var retryMinutes = Math.Min(60, Math.Pow(2, Math.Min(delivery.AttemptCount, 6)));
                await _repository.FailDeliveryAsync(
                    delivery.Id,
                    ex.Message,
                    _clock.Now.AddMinutes(retryMinutes),
                    cancellationToken);
            }
            processed++;
        }
        return processed;
    }
}
