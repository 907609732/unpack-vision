using UnpackVision.Infrastructure;

namespace UnpackVision.Service;

public sealed class SyncWorkerService : BackgroundService
{
    private readonly SyncDispatcher _dispatcher;
    private readonly ILogger<SyncWorkerService> _logger;

    public SyncWorkerService(SyncDispatcher dispatcher, ILogger<SyncWorkerService> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var count = await _dispatcher.ProcessDueAsync(20, stoppingToken);
                if (count > 0)
                {
                    _logger.LogInformation("处理了 {Count} 个同步任务", count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "同步工作线程发生错误");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
