using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class MultiCameraPreviewFrameEventArgs(
    string cameraId,
    string displayName,
    byte[] jpegBytes,
    DateTimeOffset capturedAt,
    string? trackingNo) : EventArgs
{
    public string CameraId { get; } = cameraId;
    public string DisplayName { get; } = displayName;
    public byte[] JpegBytes { get; } = jpegBytes;
    public DateTimeOffset CapturedAt { get; } = capturedAt;
    public string? TrackingNo { get; } = trackingNo;
}

/// <summary>
/// Owns one capture pipeline per configured camera and a single recording writer loop.
/// Capture callbacks only replace the latest cloned frame under each pipeline lock; the
/// writer loop is the sole owner of VideoWriter instances. This prevents native frame
/// disposal and writer finalization from racing during stop or reconnect.
/// </summary>
public sealed class MultiCameraRecordingBackend : IRecordingBackend
{
    private sealed class Pipeline(CameraProfile profile, OpenCvRecordingBackend backend)
    {
        public CameraProfile Profile { get; } = profile;
        public OpenCvRecordingBackend Backend { get; } = backend;
        public object FrameSync { get; } = new();
        public Mat? LatestFrame { get; set; }
        public DateTimeOffset LastFrameAt { get; set; }
        public CameraConnectionState State { get; set; } = CameraConnectionState.Connecting;
        public string? LastError { get; set; }
        public DateTimeOffset? GapStartedAt { get; set; }
        public bool ReconnectRunning { get; set; }
        public int PreviewCounter { get; set; }
        public int PreviewEncoding;
        public long CapturedFrames { get; set; }
        public long DroppedFrames { get; set; }
        public long WrittenFrames { get; set; }
    }

    private sealed class ActiveRecording
    {
        public required RecordingSession Session { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required Dictionary<string, VideoWriter?> Writers { get; init; }
        public required Dictionary<string, string> TemporaryPaths { get; init; }
        public required Dictionary<string, Size> OutputSizes { get; init; }
        public VideoWriter? CompositeWriter { get; set; }
        public required string CompositeTemporaryPath { get; init; }
        public required Task WriterLoop { get; set; }
        public List<MediaGap> Gaps { get; } = [];
        public IReadOnlyList<RecordTagAssignment> IssueTags { get; set; } = [];
        public Exception? PrimaryFailure { get; set; }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StorageOptions _storageOptions;
    private readonly CameraRigOptions _rig;
    private readonly List<Pipeline> _pipelines;
    private readonly ConcurrentDictionary<string, Task> _reconnectTasks = new(StringComparer.OrdinalIgnoreCase);
    private ActiveRecording? _active;
    private string? _selectedCameraId;
    private bool _disposed;

    public MultiCameraRecordingBackend(StorageOptions storageOptions, CameraRigOptions rig)
    {
        _storageOptions = storageOptions;
        _rig = NormalizeRig(rig);
        _pipelines = _rig.EnabledCameras.Select(profile =>
        {
            var backend = new OpenCvRecordingBackend(
                storageOptions,
                ToCameraOptions(profile),
                profile.Id,
                profile.DisplayName,
                previewFrameStride: 1,
                initialRotationQuarterTurns: profile.RotationQuarterTurns,
                initialMirror: profile.Mirror);
            var pipeline = new Pipeline(profile, backend);
            backend.RawFrameReady += (_, args) => OnRawFrame(pipeline, args);
            backend.CameraError += (_, args) => OnCameraError(pipeline, args.Error);
            return pipeline;
        }).ToList();
        _selectedCameraId = _rig.PrimaryCamera?.Id ?? _pipelines.FirstOrDefault()?.Profile.Id;
    }

    public event EventHandler<MultiCameraPreviewFrameEventArgs>? PreviewFrameReady;
    public event EventHandler<CameraRuntimeState>? CameraStateChanged;

    public bool IsPreviewing => _pipelines.Any(pipeline => pipeline.Backend.IsPreviewing);
    public bool IsRecording => _active is not null;
    public CameraRigOptions Rig => _rig;
    public string? SelectedCameraId => _selectedCameraId;
    public IReadOnlyList<CameraRuntimeState> RuntimeStates => _pipelines.Select(ToRuntimeState).ToArray();

    public async Task StartPreviewAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        foreach (var pipeline in _pipelines)
        {
            pipeline.State = CameraConnectionState.Connecting;
            PublishState(pipeline);
            try
            {
                await pipeline.Backend.StartPreviewAsync(cancellationToken);
                pipeline.State = CameraConnectionState.Connected;
                pipeline.LastError = null;
                PublishState(pipeline);
            }
            catch (Exception ex)
            {
                pipeline.State = pipeline.Profile.IsPrimary ? CameraConnectionState.Failed : CameraConnectionState.Missing;
                pipeline.LastError = ex.Message;
                PublishState(pipeline);
                if (pipeline.Profile.IsPrimary)
                {
                    throw new InvalidOperationException($"主机位“{pipeline.Profile.DisplayName}”连接失败：{ex.Message}", ex);
                }
                StartReconnect(pipeline);
            }
        }
    }

    public void SelectCamera(string cameraId)
    {
        if (_pipelines.All(pipeline => !string.Equals(pipeline.Profile.Id, cameraId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("机位不存在", nameof(cameraId));
        }
        _selectedCameraId = cameraId;
    }

    public async Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_active is not null)
            {
                throw new InvalidOperationException("多机位已经在录制");
            }
            if (!IsPreviewing)
            {
                await StartPreviewAsync(cancellationToken);
            }
            var primary = _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);
            if (primary.State != CameraConnectionState.Connected || primary.LastFrameAt == default)
            {
                throw new InvalidOperationException($"主机位“{primary.Profile.DisplayName}”没有有效画面");
            }

            var startedAt = DateTimeOffset.Now;
            var modeDirectory = workflow == WorkflowMode.Unpacking ? "Unpacking" : "Packing";
            var outputDirectory = Path.Combine(_storageOptions.RecordingRoot, modeDirectory);
            Directory.CreateDirectory(outputDirectory);
            var safeTracking = RecordingFileNameService.SanitizePart(trackingNo);
            var writers = new Dictionary<string, VideoWriter?>(StringComparer.OrdinalIgnoreCase);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pipeline in _pipelines)
                {
                    var runtime = pipeline.Backend.RuntimeInfo;
                    var width = runtime?.Width > 0 ? runtime.Width : pipeline.Profile.Width;
                    var height = runtime?.Height > 0 ? runtime.Height : pipeline.Profile.Height;
                    if (pipeline.Profile.RotationQuarterTurns % 2 != 0)
                    {
                        (width, height) = (height, width);
                    }
                    var size = new Size(Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));
                    var path = Path.Combine(outputDirectory,
                        $"{safeTracking}_{startedAt:yyyyMMddHHmmss}_{recordId:N}_{pipeline.Profile.Id}.partial.mp4");
                    try
                    {
                        writers[pipeline.Profile.Id] = OpenWriter(path, pipeline.Profile.Codec, pipeline.Profile.FramesPerSecond, size);
                    }
                    catch when (!pipeline.Profile.IsPrimary)
                    {
                        writers[pipeline.Profile.Id] = null;
                        pipeline.State = CameraConnectionState.Failed;
                        pipeline.LastError = "副机位编码器启动失败";
                        PublishState(pipeline);
                    }
                    paths[pipeline.Profile.Id] = path;
                    sizes[pipeline.Profile.Id] = size;
                }
                var compositePath = Path.Combine(outputDirectory,
                    $"{safeTracking}_{startedAt:yyyyMMddHHmmss}_{recordId:N}_composite.partial.mp4");
                VideoWriter? compositeWriter = null;
                try
                {
                    compositeWriter = OpenWriter(
                        compositePath,
                        "mp4v",
                        _rig.CompositeFramesPerSecond,
                        new Size(_rig.CompositeWidth, _rig.CompositeHeight));
                }
                catch
                {
                    // Independent primary recording remains authoritative. A failed
                    // composite encoder is reported as partial media at completion.
                }
                var session = new RecordingSession(recordId, trackingNo, workflow, startedAt, paths[primary.Profile.Id]);
                // The command/request token only governs startup. Recording lifetime
                // is owned by Stop/Abort so an HTTP request ending cannot cancel video.
                var cts = new CancellationTokenSource();
                var active = new ActiveRecording
                {
                    Session = session,
                    Cancellation = cts,
                    Writers = writers,
                    TemporaryPaths = paths,
                    OutputSizes = sizes,
                    CompositeWriter = compositeWriter,
                    CompositeTemporaryPath = compositePath,
                    WriterLoop = Task.CompletedTask
                };
                _active = active;
                active.WriterLoop = Task.Run(() => WriterLoopAsync(active), CancellationToken.None);
                return session;
            }
            catch
            {
                foreach (var writer in writers.Values) writer?.Dispose();
                foreach (var path in paths.Values) TryDelete(path);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingCompletion> StopAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var active = EnsureActive(session);
            active.Cancellation.Cancel();
            await active.WriterLoop;
            FinalizeWriters(active);
            _active = null;
            if (active.PrimaryFailure is not null)
            {
                throw new InvalidOperationException("主机位中断超过5秒，录像已保留为可恢复临时文件", active.PrimaryFailure);
            }

            var endedAt = DateTimeOffset.Now;
            foreach (var pipeline in _pipelines)
            {
                if (pipeline.GapStartedAt is { } started)
                {
                    active.Gaps.Add(new MediaGap
                    {
                        CameraId = pipeline.Profile.Id,
                        StartedAt = started,
                        EndedAt = endedAt,
                        Recovered = false,
                        Reason = pipeline.LastError ?? "机位在录像结束时仍未恢复"
                    });
                    pipeline.GapStartedAt = null;
                }
            }
            var primary = _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);
            var primaryPath = RecordingFileNameService.GetAvailableFinalPath(
                Path.GetDirectoryName(session.TemporaryPath)!,
                session.TrackingNo,
                session.StartedAt,
                endedAt,
                active.IssueTags,
                session.RecordId);
            var assets = new List<RecordMediaAsset>();
            foreach (var pipeline in _pipelines)
            {
                var temporary = active.TemporaryPaths[pipeline.Profile.Id];
                var role = pipeline.Profile.IsPrimary ? RecordMediaRole.Primary : RecordMediaRole.Angle;
                var final = role == RecordMediaRole.Primary
                    ? primaryPath
                    : RecordingFileNameService.GetAvailableMediaPath(primaryPath, role, pipeline.Profile.DisplayName, session.RecordId);
                var writerFailed = active.Writers[pipeline.Profile.Id] is null;
                var integrity = writerFailed
                    ? MediaIntegrityStatus.Failed
                    : active.Gaps.Any(gap => gap.CameraId == pipeline.Profile.Id)
                        ? MediaIntegrityStatus.Partial
                        : MediaIntegrityStatus.Complete;
                if (!writerFailed)
                {
                    MoveCompletedFile(temporary, final);
                }
                var runtime = pipeline.Backend.RuntimeInfo;
                assets.Add(new RecordMediaAsset
                {
                    RecordId = session.RecordId,
                    CameraId = pipeline.Profile.Id,
                    DisplayName = pipeline.Profile.DisplayName,
                    Role = role,
                    VideoPath = final,
                    Width = runtime?.Width ?? pipeline.Profile.Width,
                    Height = runtime?.Height ?? pipeline.Profile.Height,
                    FramesPerSecond = pipeline.Profile.FramesPerSecond,
                    Integrity = integrity,
                    FailureReason = writerFailed ? pipeline.LastError ?? "副机位编码器失败" : null,
                    CreatedAt = session.StartedAt,
                    UpdatedAt = endedAt
                });
            }

            var compositePath = RecordingFileNameService.GetAvailableMediaPath(
                primaryPath,
                RecordMediaRole.Composite,
                "多机位",
                session.RecordId);
            var compositeFailed = active.CompositeWriter is null;
            if (!compositeFailed)
            {
                MoveCompletedFile(active.CompositeTemporaryPath, compositePath);
            }
            var overall = compositeFailed || assets.Any(asset => asset.Integrity != MediaIntegrityStatus.Complete) || active.Gaps.Count > 0
                ? MediaIntegrityStatus.Partial
                : MediaIntegrityStatus.Complete;
            var composite = new RecordMediaAsset
            {
                RecordId = session.RecordId,
                CameraId = "composite",
                DisplayName = "多机位合成",
                Role = RecordMediaRole.Composite,
                VideoPath = compositePath,
                Width = _rig.CompositeWidth,
                Height = _rig.CompositeHeight,
                FramesPerSecond = _rig.CompositeFramesPerSecond,
                Integrity = compositeFailed ? MediaIntegrityStatus.Failed : overall,
                FailureReason = compositeFailed ? "多机位合成编码器启动失败" : null,
                CreatedAt = session.StartedAt,
                UpdatedAt = endedAt
            };
            assets.Add(composite);
            foreach (var gap in active.Gaps)
            {
                gap.RecordId = session.RecordId;
                gap.MediaAssetId = assets.FirstOrDefault(asset => asset.CameraId == gap.CameraId)?.Id;
            }
            return new RecordingCompletion(primaryPath, endedAt, compositePath, assets, active.Gaps, overall);
        }
        catch
        {
            if (_active is { } active)
            {
                active.Cancellation.Cancel();
                FinalizeWriters(active);
                _active = null;
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active?.Session.RecordId != session.RecordId) return;
            var active = _active;
            active.Cancellation.Cancel();
            await active.WriterLoop;
            FinalizeWriters(active);
            foreach (var path in active.TemporaryPaths.Values) TryDelete(path);
            TryDelete(active.CompositeTemporaryPath);
            _active = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task UpdateIssueOverlayAsync(Guid recordId, IReadOnlyList<RecordTagAssignment> activeTags, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active?.Session.RecordId != recordId) throw new InvalidOperationException("当前录像与异常标签记录不一致");
        _active.IssueTags = activeTags.Where(tag => tag.IsActive).OrderBy(tag => tag.TaggedAt).ToArray();
        return Task.CompletedTask;
    }

    public async Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.FirstOrDefault(item => item.Profile.Id == _selectedCameraId)
            ?? _pipelines.Single(item => item.Profile.IsPrimary);
        return await pipeline.Backend.TakeSnapshotAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> TakeAllSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        foreach (var pipeline in _pipelines.Where(item => item.State == CameraConnectionState.Connected))
        {
            paths.Add(await pipeline.Backend.TakeSnapshotAsync(cancellationToken));
        }
        return paths;
    }

    public async Task RotateLeftAsync(CancellationToken cancellationToken = default)
    {
        var selected = Selected();
        await selected.Backend.RotateLeftAsync(cancellationToken);
        selected.Profile.RotationQuarterTurns = (selected.Profile.RotationQuarterTurns + 3) % 4;
    }
    public async Task RotateRightAsync(CancellationToken cancellationToken = default)
    {
        var selected = Selected();
        await selected.Backend.RotateRightAsync(cancellationToken);
        selected.Profile.RotationQuarterTurns = (selected.Profile.RotationQuarterTurns + 1) % 4;
    }
    public async Task ToggleMirrorAsync(CancellationToken cancellationToken = default)
    {
        var selected = Selected();
        await selected.Backend.ToggleMirrorAsync(cancellationToken);
        selected.Profile.Mirror = !selected.Profile.Mirror;
    }
    public Task SetAutoFocusAsync(bool enabled, CancellationToken cancellationToken = default) => Selected().Backend.SetAutoFocusAsync(enabled, cancellationToken);
    public Task FocusOnceAsync(CancellationToken cancellationToken = default) => Selected().Backend.FocusOnceAsync(cancellationToken);
    public Task ApplyImageControlsAsync(double brightness, double contrast, double sharpness, double saturation, CancellationToken cancellationToken = default) =>
        Selected().Backend.ApplyImageControlsAsync(brightness, contrast, sharpness, saturation, cancellationToken);

    public async Task ConfigureCameraAsync(
        string cameraId,
        CameraOptions cameraOptions,
        bool restartPreview,
        CancellationToken cancellationToken = default)
    {
        if (IsRecording) throw new InvalidOperationException("录像过程中不能切换机位来源");
        var pipeline = _pipelines.FirstOrDefault(item => string.Equals(item.Profile.Id, cameraId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("机位不存在", nameof(cameraId));
        await pipeline.Backend.ConfigureCameraAsync(cameraOptions, restartPreview, cancellationToken);
    }

    public async Task<CameraRigPerformanceResult> TestPerformanceAsync(
        IProgress<CameraPerformanceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cameraCount = _pipelines.Count;
        progress?.Report(new CameraPerformanceProgress(2, $"正在连接 {cameraCount} 个机位"));
        await StartPreviewAsync(cancellationToken);
        await ReportTimedStageAsync(
            progress,
            "正在预热摄像头",
            startPercent: 5,
            endPercent: 38,
            durationSeconds: 5,
            cancellationToken);
        var starts = _pipelines.ToDictionary(
            pipeline => pipeline.Profile.Id,
            pipeline => (Captured: pipeline.CapturedFrames, Dropped: pipeline.DroppedFrames));
        var stopwatch = Stopwatch.StartNew();
        var testDirectory = Path.Combine(_storageOptions.RecordingRoot, ".unpackvision", "performance");
        Directory.CreateDirectory(testDirectory);
        var probePath = Path.Combine(testDirectory, $"write-{Guid.NewGuid():N}.tmp");
        const int probeBytes = 8 * 1024 * 1024;
        try
        {
            progress?.Report(new CameraPerformanceProgress(42, "正在检查磁盘写入速度"));
            var diskStopwatch = Stopwatch.StartNew();
            await using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new byte[probeBytes], cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            diskStopwatch.Stop();
            await ReportTimedStageAsync(
                progress,
                "正在测量帧率和丢帧",
                startPercent: 45,
                endPercent: 94,
                durationSeconds: 10,
                cancellationToken);
            stopwatch.Stop();
            progress?.Report(new CameraPerformanceProgress(97, "正在分析测试结果"));
            var cameraResults = _pipelines.Select(pipeline =>
            {
                var frames = pipeline.CapturedFrames - starts[pipeline.Profile.Id].Captured;
                var droppedFrames = pipeline.DroppedFrames - starts[pipeline.Profile.Id].Dropped;
                var actual = frames / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
                var target = pipeline.Profile.FramesPerSecond;
                var dropped = droppedFrames / (double)Math.Max(1, frames + droppedFrames);
                var passed = pipeline.State == CameraConnectionState.Connected && actual >= target * 0.9 && dropped < 0.01;
                return new CameraPerformanceResult(
                    pipeline.Profile.Id,
                    pipeline.Profile.DisplayName,
                    passed,
                    target,
                    actual,
                    dropped,
                    EstimateMegabits(pipeline.Profile),
                    passed ? "通过" : $"实际 {actual:F1}fps，请降低该机位分辨率");
            }).ToArray();
            var bitrate = cameraResults.Sum(item => item.EstimatedMegabitsPerSecond) + 8;
            var megabytesPerHour = bitrate * 3600 / 8;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_storageOptions.RecordingRoot))!);
            var availableHours = drive.AvailableFreeSpace * 0.75 / (megabytesPerHour * 1024 * 1024);
            var writeMegabytesPerSecond = probeBytes / 1024d / 1024d / Math.Max(0.01, diskStopwatch.Elapsed.TotalSeconds);
            var diskPassed = writeMegabytesPerSecond * 8 >= bitrate * 1.25;
            var passed = diskPassed && cameraResults.All(item => item.Passed);
            var result = new CameraRigPerformanceResult(
                passed,
                cameraResults,
                megabytesPerHour,
                availableHours,
                passed ? "全部机位性能测试通过" : "性能不足，已阻止启用；请按提示降低指定机位分辨率");
            progress?.Report(new CameraPerformanceProgress(100, passed ? "测试完成，配置可用" : "测试完成，请查看改进建议"));
            return result;
        }
        finally
        {
            TryDelete(probePath);
        }
    }

    private static async Task ReportTimedStageAsync(
        IProgress<CameraPerformanceProgress>? progress,
        string stage,
        int startPercent,
        int endPercent,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        for (var elapsed = 0; elapsed < durationSeconds; elapsed++)
        {
            var percent = startPercent +
                          (int)Math.Round((endPercent - startPercent) * elapsed / (double)durationSeconds);
            progress?.Report(new CameraPerformanceProgress(percent, stage, durationSeconds - elapsed));
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        progress?.Report(new CameraPerformanceProgress(endPercent, stage, 0));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_active is { } active)
            {
                active.Cancellation.Cancel();
                try { await active.WriterLoop; } catch { }
                FinalizeWriters(active);
            }
            foreach (var pipeline in _pipelines)
            {
                await pipeline.Backend.DisposeAsync();
                lock (pipeline.FrameSync)
                {
                    pipeline.LatestFrame?.Dispose();
                    pipeline.LatestFrame = null;
                }
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task WriterLoopAsync(ActiveRecording active)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Math.Max(1, _rig.CompositeFramesPerSecond)));
        try
        {
            while (await timer.WaitForNextTickAsync(active.Cancellation.Token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.Now;
                var rendered = new List<(Pipeline Pipeline, Mat Frame)>();
                try
                {
                    foreach (var pipeline in _pipelines)
                    {
                        Mat frame;
                        var missing = false;
                        lock (pipeline.FrameSync)
                        {
                            missing = pipeline.LatestFrame is null || now - pipeline.LastFrameAt > TimeSpan.FromSeconds(1);
                            frame = missing
                                ? CreatePlaceholder(active.OutputSizes[pipeline.Profile.Id], pipeline.Profile.DisplayName)
                                : pipeline.LatestFrame!.Clone();
                        }
                        if (missing) BeginGap(active, pipeline, now);
                        else EndGap(active, pipeline, now);
                        using var resized = ResizeTo(frame, active.OutputSizes[pipeline.Profile.Id]);
                        frame.Dispose();
                        RecordingOverlayRenderer.Draw(resized, active.Session, active.IssueTags);
                        if (active.Writers[pipeline.Profile.Id] is { } writer)
                        {
                            try
                            {
                                writer.Write(resized);
                                pipeline.WrittenFrames++;
                            }
                            catch (Exception ex)
                            {
                                writer.Dispose();
                                active.Writers[pipeline.Profile.Id] = null;
                                pipeline.LastError = ex.Message;
                                pipeline.State = CameraConnectionState.Failed;
                                if (pipeline.Profile.IsPrimary) active.PrimaryFailure ??= ex;
                                PublishState(pipeline);
                            }
                        }
                        rendered.Add((pipeline, resized.Clone()));
                    }
                    using var composite = ComposeGrid(rendered, new Size(_rig.CompositeWidth, _rig.CompositeHeight));
                    if (active.CompositeWriter is { } compositeWriter)
                    {
                        try
                        {
                            compositeWriter.Write(composite);
                        }
                        catch
                        {
                            compositeWriter.Dispose();
                            active.CompositeWriter = null;
                        }
                    }
                }
                finally
                {
                    foreach (var item in rendered) item.Frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
        }
    }

    private void BeginGap(ActiveRecording active, Pipeline pipeline, DateTimeOffset now)
    {
        pipeline.GapStartedAt ??= now;
        if (pipeline.Profile.IsPrimary && now - pipeline.GapStartedAt >= TimeSpan.FromSeconds(5))
        {
            active.PrimaryFailure ??= new IOException($"主机位“{pipeline.Profile.DisplayName}”已中断超过5秒");
        }
    }

    private static void EndGap(ActiveRecording active, Pipeline pipeline, DateTimeOffset now)
    {
        if (pipeline.GapStartedAt is not { } started) return;
        active.Gaps.Add(new MediaGap
        {
            CameraId = pipeline.Profile.Id,
            StartedAt = started,
            EndedAt = now,
            Recovered = true,
            Reason = pipeline.LastError ?? "机位画面中断"
        });
        pipeline.GapStartedAt = null;
    }

    private void OnRawFrame(Pipeline pipeline, RawCameraFrameEventArgs args)
    {
        lock (pipeline.FrameSync)
        {
            if (pipeline.LastFrameAt != default)
            {
                var elapsed = (args.CapturedAt - pipeline.LastFrameAt).TotalSeconds;
                var expectedFrames = elapsed * Math.Max(1, pipeline.Profile.FramesPerSecond);
                pipeline.DroppedFrames += Math.Max(0, (long)Math.Round(expectedFrames) - 1);
            }
            pipeline.LatestFrame?.Dispose();
            pipeline.LatestFrame = args.Frame.Clone();
            pipeline.LastFrameAt = args.CapturedAt;
            pipeline.CapturedFrames++;
        }
        if (pipeline.State != CameraConnectionState.Connected)
        {
            pipeline.State = CameraConnectionState.Connected;
            pipeline.LastError = null;
            PublishState(pipeline);
        }
        if (++pipeline.PreviewCounter % 3 != 0 || Interlocked.Exchange(ref pipeline.PreviewEncoding, 1) != 0) return;
        var previewSource = args.Frame.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                using (previewSource)
                using (var preview = ResizeForPreview(previewSource, 960, 540))
                {
                    Cv2.ImEncode(".jpg", preview, out var bytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 80)]);
                    PreviewFrameReady?.Invoke(this, new MultiCameraPreviewFrameEventArgs(
                        pipeline.Profile.Id,
                        pipeline.Profile.DisplayName,
                        bytes,
                        args.CapturedAt,
                        _active?.Session.TrackingNo));
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref pipeline.PreviewEncoding, 0);
            }
        });
    }

    private void OnCameraError(Pipeline pipeline, Exception error)
    {
        pipeline.State = CameraConnectionState.Reconnecting;
        pipeline.LastError = error.Message;
        PublishState(pipeline);
        StartReconnect(pipeline);
    }

    private void StartReconnect(Pipeline pipeline)
    {
        _reconnectTasks.GetOrAdd(pipeline.Profile.Id, cameraKey => Task.Run(async () =>
        {
            pipeline.ReconnectRunning = true;
            try
            {
                foreach (var delay in new[] { 1, 2, 5 })
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    if (_disposed || pipeline.State == CameraConnectionState.Connected) return;
                    try
                    {
                        await pipeline.Backend.StartPreviewAsync();
                        pipeline.State = CameraConnectionState.Connected;
                        pipeline.LastError = null;
                        PublishState(pipeline);
                        return;
                    }
                    catch (Exception ex)
                    {
                        pipeline.LastError = ex.Message;
                    }
                }
                pipeline.State = pipeline.Profile.IsPrimary ? CameraConnectionState.Failed : CameraConnectionState.Missing;
                PublishState(pipeline);
            }
            finally
            {
                pipeline.ReconnectRunning = false;
                _reconnectTasks.TryRemove(cameraKey, out var _);
            }
        }));
    }

    private static Mat ComposeGrid(IReadOnlyList<(Pipeline Pipeline, Mat Frame)> frames, Size outputSize)
    {
        var canvas = new Mat(outputSize, MatType.CV_8UC3, new Scalar(18, 18, 20));
        var columns = frames.Count == 1 ? 1 : 2;
        var rows = frames.Count <= 2 ? 1 : 2;
        var cellWidth = outputSize.Width / columns;
        var cellHeight = outputSize.Height / rows;
        for (var index = 0; index < frames.Count; index++)
        {
            var x = index % columns * cellWidth;
            var y = index / columns * cellHeight;
            using var fitted = Letterbox(frames[index].Frame, new Size(cellWidth, cellHeight));
            fitted.CopyTo(new Mat(canvas, new Rect(x, y, cellWidth, cellHeight)));
            Cv2.Rectangle(canvas, new Rect(x, y, cellWidth, cellHeight), new Scalar(85, 85, 90), 1);
            Cv2.PutText(canvas, frames[index].Pipeline.Profile.DisplayName, new Point(x + 18, y + 34),
                HersheyFonts.HersheySimplex, 0.8, Scalar.White, 2, LineTypes.AntiAlias);
        }
        return canvas;
    }

    private static Mat Letterbox(Mat source, Size target)
    {
        var result = new Mat(target, MatType.CV_8UC3, Scalar.Black);
        var scale = Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
        var width = Math.Max(2, (int)(source.Width * scale) / 2 * 2);
        var height = Math.Max(2, (int)(source.Height * scale) / 2 * 2);
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(width, height), interpolation: InterpolationFlags.Area);
        resized.CopyTo(new Mat(result, new Rect((target.Width - width) / 2, (target.Height - height) / 2, width, height)));
        return result;
    }

    private static Mat ResizeTo(Mat source, Size target)
    {
        if (source.Size() == target) return source.Clone();
        var result = new Mat();
        Cv2.Resize(source, result, target, interpolation: InterpolationFlags.Area);
        return result;
    }

    private static Mat ResizeForPreview(Mat frame, int maximumWidth, int maximumHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maximumWidth / frame.Width, (double)maximumHeight / frame.Height));
        if (scale >= 0.999) return frame.Clone();
        var resized = new Mat();
        Cv2.Resize(frame, resized, new Size((int)(frame.Width * scale), (int)(frame.Height * scale)), interpolation: InterpolationFlags.Area);
        return resized;
    }

    private static Mat CreatePlaceholder(Size size, string displayName)
    {
        var frame = new Mat(size, MatType.CV_8UC3, new Scalar(30, 30, 34));
        Cv2.PutText(frame, "CAMERA OFFLINE", new Point(Math.Max(20, size.Width / 8), size.Height / 2),
            HersheyFonts.HersheySimplex, Math.Max(0.8, size.Width / 1600d), new Scalar(180, 180, 185), 2, LineTypes.AntiAlias);
        Cv2.PutText(frame, displayName, new Point(Math.Max(20, size.Width / 8), size.Height / 2 + 55),
            HersheyFonts.HersheySimplex, Math.Max(0.6, size.Width / 2100d), new Scalar(125, 125, 130), 2, LineTypes.AntiAlias);
        return frame;
    }

    private static VideoWriter OpenWriter(string path, string codecName, double fps, Size size)
    {
        var codec = (string.IsNullOrWhiteSpace(codecName) ? "mp4v" : codecName).PadRight(4, ' ').AsSpan(0, 4);
        var writer = new VideoWriter(path, VideoWriter.FourCC(codec[0], codec[1], codec[2], codec[3]), Math.Max(1, fps), size);
        if (!writer.IsOpened())
        {
            writer.Dispose();
            throw new InvalidOperationException($"无法创建 {size.Width}×{size.Height} MP4编码器（{codecName}）");
        }
        return writer;
    }

    private static void FinalizeWriters(ActiveRecording active)
    {
        foreach (var writer in active.Writers.Values)
        {
            writer?.Release();
            writer?.Dispose();
        }
        active.CompositeWriter?.Release();
        active.CompositeWriter?.Dispose();
        active.Cancellation.Dispose();
    }

    private static void MoveCompletedFile(string temporary, string final)
    {
        if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            throw new InvalidDataException($"录像文件为空：{Path.GetFileName(temporary)}");
        File.Move(temporary, final, false);
    }

    private Pipeline Selected() => _pipelines.FirstOrDefault(pipeline => pipeline.Profile.Id == _selectedCameraId)
        ?? _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);

    private ActiveRecording EnsureActive(RecordingSession session) =>
        _active?.Session.RecordId == session.RecordId ? _active : throw new InvalidOperationException("录像会话已经失效");

    private CameraRuntimeState ToRuntimeState(Pipeline pipeline)
    {
        var runtime = pipeline.Backend.RuntimeInfo;
        var dropped = pipeline.CapturedFrames == 0
            ? 0
            : pipeline.DroppedFrames / (double)Math.Max(1, pipeline.CapturedFrames + pipeline.DroppedFrames);
        return new CameraRuntimeState(
            pipeline.Profile.Id,
            pipeline.Profile.DisplayName,
            pipeline.Profile.IsPrimary,
            pipeline.State,
            runtime?.Width ?? 0,
            runtime?.Height ?? 0,
            runtime?.FramesPerSecond ?? 0,
            dropped,
            pipeline.LastError);
    }

    private void PublishState(Pipeline pipeline)
    {
        try { CameraStateChanged?.Invoke(this, ToRuntimeState(pipeline)); } catch { }
    }

    private static CameraRigOptions NormalizeRig(CameraRigOptions rig)
    {
        if (rig.EnabledCameras.Count == 0) throw new InvalidOperationException("至少需要启用一个机位");
        if (rig.EnabledCameras.Count > CameraRigOptions.MaximumEnabledCameras) throw new InvalidOperationException("最多只能启用4个机位");
        if (rig.EnabledCameras.Count(camera => camera.IsPrimary) != 1) throw new InvalidOperationException("必须且只能设置一个主机位");
        return rig;
    }

    private static CameraOptions ToCameraOptions(CameraProfile profile) => new()
    {
        SourceKind = (CameraSourceKind)profile.SourceType,
        CameraIndex = profile.LegacyCameraIndex,
        WindowsSymbolicLink = profile.WindowsSymbolicLink,
        AutoSelectBestCamera = profile.AutoSelectBestCamera,
        Width = profile.Width,
        Height = profile.Height,
        FramesPerSecond = profile.FramesPerSecond,
        Codec = profile.Codec,
        Brightness = profile.Brightness,
        Contrast = profile.Contrast,
        Sharpness = profile.Sharpness,
        Saturation = profile.Saturation,
        AutoFocus = profile.AutoFocus,
        NetworkStreamUrl = profile.NetworkStreamUrl,
        NetworkUsername = profile.NetworkUsername,
        NetworkPasswordProtected = profile.NetworkPasswordProtected,
        HikvisionHost = profile.HikvisionHost,
        HikvisionHttpPort = profile.HikvisionHttpPort,
        HikvisionRtspPort = profile.HikvisionRtspPort,
        HikvisionChannel = profile.HikvisionChannel,
        HikvisionSubStream = profile.HikvisionSubStream
    };

    private static double EstimateMegabits(CameraProfile profile) => Math.Max(2, profile.Width * profile.Height * profile.FramesPerSecond * 0.08 / 1_000_000d);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
