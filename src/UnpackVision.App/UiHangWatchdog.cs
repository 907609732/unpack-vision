using System.Diagnostics;
using System.Windows.Threading;
using UnpackVision.Infrastructure.Diagnostics;

namespace UnpackVision.App;

/// <summary>
/// Detects a blocked WPF dispatcher from a background thread. It records one
/// warning per stall and a recovery event instead of flooding the log.
/// </summary>
internal sealed class UiHangWatchdog : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(5);
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationSync = new();
    private readonly Task _watchTask;
    private string _currentOperation = "idle";
    private bool _disposed;

    internal UiHangWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _watchTask = Task.Run(WatchAsync);
    }

    internal IDisposable BeginOperation(string operation)
    {
        lock (_operationSync)
        {
            var previous = _currentOperation;
            _currentOperation = operation;
            return new OperationScope(this, previous);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task WatchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(ProbeInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                var started = Stopwatch.GetTimestamp();
                var probe = _dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Send,
                    _lifetime.Token).Task;
                var first = await Task.WhenAny(
                    probe,
                    Task.Delay(HangThreshold, _lifetime.Token));
                if (first == probe)
                {
                    await probe;
                    continue;
                }

                var operation = GetCurrentOperation();
                DiagnosticLog.Warning(
                    "检测到界面线程超过 {ThresholdSeconds} 秒没有响应，当前操作 {UiOperation}",
                    HangThreshold.TotalSeconds,
                    operation);

                await probe.WaitAsync(_lifetime.Token);
                DiagnosticLog.Information(
                    "界面线程已恢复，阻塞约 {ElapsedMilliseconds} 毫秒，操作 {UiOperation}",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    operation);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "界面卡顿看门狗异常退出");
        }
    }

    private string GetCurrentOperation()
    {
        lock (_operationSync)
        {
            return _currentOperation;
        }
    }

    private sealed class OperationScope(UiHangWatchdog owner, string previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (owner._operationSync)
            {
                owner._currentOperation = previous;
            }
        }
    }
}
