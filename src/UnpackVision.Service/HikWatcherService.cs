using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenCvSharp;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Service;

public sealed class HikWatcherService : BackgroundService
{
    private const string BaselineKey = "hik.compatibility.baseline.v1";
    private readonly Channel<(string Path, WorkflowMode Workflow)> _queue =
        Channel.CreateUnbounded<(string, WorkflowMode)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HikCompatibilityOptions _options;
    private readonly HikRecordingImporter _importer;
    private readonly IScanRecordRepository _repository;
    private readonly ILogger<HikWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];

    public HikWatcherService(
        HikCompatibilityOptions options,
        HikRecordingImporter importer,
        IScanRecordRepository repository,
        ILogger<HikWatcherService> logger)
    {
        _options = options;
        _importer = importer;
        _repository = repository;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baselineExists = await _repository.GetMetadataAsync(BaselineKey, stoppingToken) is not null;
        await ReconcileAsync(enqueueSync: baselineExists, stoppingToken);
        if (!baselineExists)
        {
            await _repository.SetMetadataAsync(BaselineKey, DateTimeOffset.Now.ToString("O"), stoppingToken);
            _logger.LogInformation("已建立 HIK SCAN 历史基线；历史录像只导入数据库，不自动写入 Excel");
        }

        AddWatcher(_options.UnpackingDirectory, WorkflowMode.Unpacking);
        AddWatcher(_options.PackingDirectory, WorkflowMode.Packing);

        var reconcileTask = ReconcileLoopAsync(stoppingToken);
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    if (await WaitUntilFinalizedAsync(item.Path, stoppingToken))
                    {
                        var record = await _importer.ImportAsync(item.Path, item.Workflow, enqueueSync: true, stoppingToken);
                        if (record is not null)
                        {
                            _logger.LogInformation("发现并导入一段新录像");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "处理 HIK SCAN 录像失败");
                }
                finally
                {
                    _queued.TryRemove(item.Path, out _);
                }
            }
        }
        finally
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            await reconcileTask;
        }
    }

    private void AddWatcher(string directory, WorkflowMode workflow)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("HIK SCAN 目录不存在，兼容导入将等待后续对账");
            return;
        }
        var watcher = new FileSystemWatcher(directory, "*.mp4")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, args) => Enqueue(args.FullPath, workflow);
        watcher.Renamed += (_, args) => Enqueue(args.FullPath, workflow);
        watcher.Error += (_, args) => _logger.LogWarning(args.GetException(), "HIK SCAN 文件监控发生错误，将由定期对账补偿");
        _watchers.Add(watcher);
    }

    private void Enqueue(string path, WorkflowMode workflow)
    {
        if (_queued.TryAdd(path, 0))
        {
            _queue.Writer.TryWrite((path, workflow));
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await ReconcileAsync(enqueueSync: true, cancellationToken);
        }
    }

    private async Task ReconcileAsync(bool enqueueSync, CancellationToken cancellationToken)
    {
        await ReconcileDirectoryAsync(_options.UnpackingDirectory, WorkflowMode.Unpacking, enqueueSync, cancellationToken);
        await ReconcileDirectoryAsync(_options.PackingDirectory, WorkflowMode.Packing, enqueueSync, cancellationToken);
    }

    private async Task ReconcileDirectoryAsync(
        string directory,
        WorkflowMode workflow,
        bool enqueueSync,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enqueueSync)
            {
                Enqueue(path, workflow);
            }
            else
            {
                await _importer.ImportAsync(path, workflow, enqueueSync: false, cancellationToken);
            }
        }
    }

    private async Task<bool> WaitUntilFinalizedAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(_options.FinalizationTimeoutSeconds);
        long lastLength = -1;
        var stable = 0;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length > 0 && file.Length == lastLength)
                {
                    stable++;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stable >= _options.StableSamples && IsPlayableMp4(path))
                    {
                        return true;
                    }
                }
                else
                {
                    stable = 0;
                }
                lastLength = file.Exists ? file.Length : -1;
            }
            catch (IOException)
            {
                stable = 0;
            }
            await Task.Delay(_options.StableSampleDelayMilliseconds, cancellationToken);
        }
        _logger.LogWarning("一段录像在等待时间内未完成写入");
        return false;
    }

    private static bool IsPlayableMp4(string path)
    {
        try
        {
            using var capture = new VideoCapture(path);
            if (!capture.IsOpened())
            {
                return false;
            }
            using var firstFrame = new Mat();
            return capture.Read(firstFrame) && !firstFrame.Empty();
        }
        catch (OpenCVException)
        {
            return false;
        }
    }
}
