using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;
using UnpackVision.Core;
using UnpackVision.Core.Recording;

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

public sealed class RecordingStorageWarningEventArgs(StorageAllocation allocation) : EventArgs
{
    public StorageAllocation Allocation { get; } = allocation;
}

public enum PrimaryRecordingFailureKind
{
    WriterFailure,
    FrameGap
}

public sealed class PrimaryRecordingFailureEventArgs(
    Guid recordId,
    string cameraId,
    string cameraName,
    PrimaryRecordingFailureKind kind,
    string message,
    Exception error) : EventArgs
{
    public Guid RecordId { get; } = recordId;
    public string CameraId { get; } = cameraId;
    public string CameraName { get; } = cameraName;
    public PrimaryRecordingFailureKind Kind { get; } = kind;
    public string Message { get; } = message;
    public Exception Error { get; } = error;
}

internal sealed class PrimaryRecordingFailureNotificationGate
{
    private int _claimed;

    public bool TryClaim(bool recordingIsStopping) =>
        !recordingIsStopping && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
}

internal readonly record struct CameraEncodingRuntimeSnapshot(
    double FramesPerSecond,
    int QueueDepth,
    double ProcessingLatencyMilliseconds,
    string? EncoderName,
    bool HardwareAccelerated);

/// <summary>
/// Tracks only the active recording writer for one camera. The writer boundary is
/// synchronous, so there is no application-level frame queue: a snapshot therefore
/// reports a queue depth of zero while still exposing measured throughput and the most
/// recent clone/overlay/write latency. Keeping this state per pipeline avoids mixing
/// metrics between consecutive recording sessions.
/// </summary>
internal sealed class CameraEncodingRuntimeMetrics
{
    private readonly object _sync = new();
    private long _startedTimestamp;
    private long _writtenFrames;
    private double _lastProcessingLatencyMilliseconds;
    private string? _encoderName;
    private bool _hardwareAccelerated;
    private bool _active;

    public void Begin(IRecordingFrameWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_sync)
        {
            _startedTimestamp = Stopwatch.GetTimestamp();
            _writtenFrames = 0;
            _lastProcessingLatencyMilliseconds = 0;
            _encoderName = writer.EncoderName;
            _hardwareAccelerated = writer.HardwareAccelerated;
            _active = true;
        }
    }

    public void RecordWrite(TimeSpan processingLatency)
    {
        lock (_sync)
        {
            if (!_active) return;
            _writtenFrames++;
            _lastProcessingLatencyMilliseconds = Math.Max(0, processingLatency.TotalMilliseconds);
        }
    }

    public void End()
    {
        lock (_sync)
        {
            _active = false;
            _startedTimestamp = 0;
            _writtenFrames = 0;
            _lastProcessingLatencyMilliseconds = 0;
            _encoderName = null;
            _hardwareAccelerated = false;
        }
    }

    public CameraEncodingRuntimeSnapshot Snapshot()
    {
        lock (_sync)
        {
            if (!_active || _startedTimestamp == 0)
            {
                return new CameraEncodingRuntimeSnapshot(0, 0, 0, null, false);
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;
            var framesPerSecond = elapsedSeconds <= 0
                ? 0
                : _writtenFrames / elapsedSeconds;
            return new CameraEncodingRuntimeSnapshot(
                framesPerSecond,
                QueueDepth: 0,
                _lastProcessingLatencyMilliseconds,
                _encoderName,
                _hardwareAccelerated);
        }
    }
}

/// <summary>
/// Owns one capture pipeline and one bounded writer loop per configured camera.
/// Capture callbacks only replace the latest cloned frame under each pipeline lock; the
/// matching writer loop is the sole owner of its VideoWriter. This prevents a slow
/// secondary route from stalling the other recordings and keeps native frame disposal
/// and writer finalization ordered during stop or reconnect.
/// </summary>
public sealed class MultiCameraRecordingBackend : IRecordingBackend
{
    private static readonly TimeSpan WriterLoopShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WriterForceTerminationGrace = TimeSpan.FromSeconds(2);
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
        public CameraEncodingRuntimeMetrics EncodingMetrics { get; } = new();
    }

    private sealed class ActiveRecording
    {
        public required RecordingSession Session { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required Dictionary<string, IRecordingFrameWriter?> Writers { get; init; }
        public required Dictionary<string, string> TemporaryPaths { get; init; }
        public required Dictionary<string, Size> OutputSizes { get; init; }
        public IRecordingFrameWriter? CompositeWriter { get; set; }
        public required string CompositeTemporaryPath { get; init; }
        public required Task WriterLoop { get; set; }
        public required RecordingStorageLease StorageLease { get; init; }
        public StorageAllocation StorageAllocation => StorageLease.Allocation;
        public ConcurrentQueue<MediaGap> Gaps { get; } = new();
        public IReadOnlyList<RecordTagAssignment> IssueTags = [];
        public Exception? PrimaryFailure;
        public PrimaryRecordingFailureNotificationGate PrimaryFailureNotification { get; } = new();
        public bool WriterShutdownTimedOut;
    }

    private sealed record EncodingProbeResult(
        string CameraId,
        long WrittenFrames,
        long MissingFrames,
        TimeSpan Elapsed,
        string FilePath,
        string? Error = null);

    private sealed record StorageProbeMeasurement(
        StoragePerformanceResult Result,
        string TestDirectory,
        long AvailableBytes);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StorageOptions _storageOptions;
    private readonly CameraRigOptions _rig;
    private readonly List<Pipeline> _pipelines;
    private readonly StoragePoolOptions _storagePoolOptions;
    private readonly IRecordingStorageAllocator _storageAllocator;
    private readonly IMediaRuntimeCapabilityProbe _mediaRuntimeProbe;
    private readonly ConcurrentDictionary<string, Task> _reconnectTasks = new(StringComparer.OrdinalIgnoreCase);
    private ActiveRecording? _active;
    private string? _selectedCameraId;
    private string? _gstreamerLaunchPath;
    private MediaEncoderCapability? _gstreamerEncoder;
    private bool _disposed;

    public MultiCameraRecordingBackend(
        StorageOptions storageOptions,
        CameraRigOptions rig,
        IRecordingStorageAllocator? storageAllocator = null,
        IMediaRuntimeCapabilityProbe? mediaRuntimeProbe = null)
    {
        _storageOptions = storageOptions;
        _storagePoolOptions = storageOptions.StoragePool.Targets.Count == 0
            ? StoragePoolOptions.FromLegacyRecordingRoot(storageOptions.RecordingRoot)
            : storageOptions.StoragePool;
        _storageAllocator = storageAllocator ?? new RecordingStoragePoolAllocator(
            new RecordingStoragePoolMonitor(new SystemStorageDiskProbe()));
        _mediaRuntimeProbe = mediaRuntimeProbe ?? new MediaRuntimeCapabilityProbe();
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
    public event EventHandler<RecordingStorageWarningEventArgs>? StorageWarningRaised;
    public event EventHandler<PrimaryRecordingFailureEventArgs>? PrimaryRecordingFailed;

    public bool IsPreviewing => _pipelines.Any(pipeline => pipeline.Backend.IsPreviewing);
    public bool IsRecording => _active is not null;
    public CameraRigOptions Rig => _rig;
    public string? SelectedCameraId => _selectedCameraId;
    public IReadOnlyList<CameraRuntimeState> RuntimeStates => _pipelines.Select(ToRuntimeState).ToArray();

    public async Task StartPreviewAsync(CancellationToken cancellationToken = default)
        => await StartPreviewCoreAsync(enforcePerformanceAdmission: true, cancellationToken);

    /// <summary>
    /// Releases all configured capture devices while retaining the station backend and its
    /// coordinator. This bounded pause is used only by the modal camera configuration preview.
    /// </summary>
    public async Task StopPreviewAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_active is not null)
            {
                throw new InvalidOperationException("录像过程中不能停止机位预览");
            }
            foreach (var pipeline in _pipelines)
            {
                await pipeline.Backend.StopPreviewAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartPreviewCoreAsync(
        bool enforcePerformanceAdmission,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (enforcePerformanceAdmission)
        {
            CameraRigPerformanceAdmission.EnsureHighChannelRigIsAdmitted(_rig, _storagePoolOptions);
        }
        if (MultiCameraRecordingPolicy.RequiresProductionRuntime(_pipelines.Count))
        {
            await EnsureProductionMediaRuntimeAsync(cancellationToken);
        }
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
            CameraRigPerformanceAdmission.EnsureHighChannelRigIsAdmitted(_rig, _storagePoolOptions);
            if (_active is not null)
            {
                throw new InvalidOperationException("多机位已经在录制");
            }
            if (!IsPreviewing)
            {
                await StartPreviewCoreAsync(enforcePerformanceAdmission: true, cancellationToken);
            }
            var primary = _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);
            if (primary.State != CameraConnectionState.Connected || primary.LastFrameAt == default)
            {
                throw new InvalidOperationException($"主机位“{primary.Profile.DisplayName}”没有有效画面");
            }

            var startedAt = DateTimeOffset.Now;
            var modeDirectory = workflow == WorkflowMode.Unpacking ? "Unpacking" : "Packing";
            var estimatedBytesPerHour = EstimateBytesPerHour(_pipelines.Select(item => item.Profile));
            var projectedMaximumBytes = (long)Math.Ceiling(
                estimatedBytesPerHour * Math.Max(1, _storageOptions.MaximumRecordingMinutes) / 60d * 1.25);
            var (allocationResult, storageLease) = await RecordingStorageLease.TryAcquireAsync(
                _storageAllocator,
                _storagePoolOptions,
                new StorageAllocationRequest(
                    recordId.ToString("N"),
                    Math.Max(1, projectedMaximumBytes),
                    Math.Max(1, estimatedBytesPerHour)),
                cancellationToken);
            if (!allocationResult.Succeeded || storageLease is null)
            {
                throw new IOException($"录像存储空间不可用：{allocationResult.Message}");
            }
            var writers = new Dictionary<string, IRecordingFrameWriter?>(StringComparer.OrdinalIgnoreCase);
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var storageAllocation = storageLease.Allocation;
                if (storageAllocation.WarningReasons != StorageWarningReason.None)
                {
                    StorageWarningRaised?.Invoke(this, new RecordingStorageWarningEventArgs(storageAllocation));
                }
                var outputDirectory = Path.Combine(storageAllocation.RootPath, modeDirectory);
                Directory.CreateDirectory(outputDirectory);
                var safeTracking = RecordingFileNameService.SanitizePart(trackingNo);
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
                var compositePath = _rig.EffectiveCompositeRecordingEnabled
                    ? Path.Combine(outputDirectory,
                        $"{safeTracking}_{startedAt:yyyyMMddHHmmss}_{recordId:N}_composite.partial.mp4")
                    : string.Empty;
                IRecordingFrameWriter? compositeWriter = null;
                try
                {
                    if (_rig.EffectiveCompositeRecordingEnabled)
                    {
                        compositeWriter = OpenWriter(
                            compositePath,
                            "mp4v",
                            _rig.CompositeFramesPerSecond,
                            new Size(_rig.CompositeWidth, _rig.CompositeHeight));
                    }
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
                    StorageLease = storageLease,
                    WriterLoop = Task.CompletedTask
                };
                _active = active;
                var writerLoops = _pipelines
                    .Select<Pipeline, Func<Task>>(pipeline => () => CameraWriterLoopAsync(active, pipeline))
                    .ToList();
                if (active.CompositeWriter is not null)
                {
                    writerLoops.Add(() => CompositeWriterLoopAsync(active));
                }
                active.WriterLoop = IndependentWriterLoopGroup.Start(writerLoops);
                return session;
            }
            catch
            {
                foreach (var writer in writers.Values) writer?.Dispose();
                foreach (var path in paths.Values) TryDelete(path);
                await storageLease.ReleaseAsync(CancellationToken.None);
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
        RecordingStorageLease? storageLease = null;
        try
        {
            var active = EnsureActive(session);
            storageLease = active.StorageLease;
            active.Cancellation.Cancel();
            await WaitForWriterLoopAsync(active);
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
                    active.Gaps.Enqueue(new MediaGap
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
            var gaps = active.Gaps.ToList();
            var primary = _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);
            var primaryPath = RecordingFileNameService.GetAvailableFinalPath(
                Path.GetDirectoryName(session.TemporaryPath)!,
                session.TrackingNo,
                session.StartedAt,
                endedAt,
                active.IssueTags,
                session.RecordId);
            var assets = new List<RecordMediaAsset>();
            // Finalize the required primary first. Optional media may remain as a
            // recoverable partial without invalidating that authoritative recording.
            foreach (var pipeline in _pipelines.OrderByDescending(item => item.Profile.IsPrimary))
            {
                var temporary = active.TemporaryPaths[pipeline.Profile.Id];
                var role = pipeline.Profile.IsPrimary ? RecordMediaRole.Primary : RecordMediaRole.Angle;
                var final = role == RecordMediaRole.Primary
                    ? primaryPath
                    : RecordingFileNameService.GetAvailableMediaPath(primaryPath, role, pipeline.Profile.DisplayName, session.RecordId);
                var writerFailed = active.Writers[pipeline.Profile.Id] is null;
                var completedWriter = active.Writers[pipeline.Profile.Id];
                if (role == RecordMediaRole.Primary && writerFailed)
                {
                    throw new InvalidOperationException(
                        $"主机位“{pipeline.Profile.DisplayName}”录像编码失败，临时文件已保留。");
                }

                var finalization = writerFailed
                    ? MultiCameraMediaFinalizationPolicy.DescribeWriterFailure(
                        temporary,
                        final,
                        pipeline.Profile.DisplayName,
                        pipeline.LastError ?? "副机位编码器启动或运行失败")
                    : MultiCameraMediaFinalizationPolicy.PromoteCompletedFile(
                        temporary,
                        final,
                        required: role == RecordMediaRole.Primary,
                        displayName: pipeline.Profile.DisplayName);
                var integrity = finalization.Integrity == MediaIntegrityStatus.Complete &&
                                gaps.Any(gap => gap.CameraId == pipeline.Profile.Id)
                    ? MediaIntegrityStatus.Partial
                    : finalization.Integrity;

                if (role == RecordMediaRole.Angle && !finalization.HasRecoverableMedia &&
                    gaps.All(gap => !string.Equals(gap.CameraId, pipeline.Profile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    gaps.Add(MultiCameraMediaFinalizationPolicy.CreateMissingMediaGap(
                        session.RecordId,
                        pipeline.Profile.Id,
                        session.StartedAt,
                        endedAt,
                        finalization.FailureReason ?? "副机位未生成可恢复录像文件。"));
                }

                var runtime = pipeline.Backend.RuntimeInfo;
                assets.Add(MultiCameraMediaAssetFactory.Create(new RecordMediaAssetSpecification(
                    session.RecordId,
                    pipeline.Profile.Id,
                    pipeline.Profile.DisplayName,
                    role,
                    finalization.AssetPath,
                    active.StorageAllocation,
                    completedWriter?.Codec ?? pipeline.Profile.Codec,
                    completedWriter?.EncoderName ?? string.Empty,
                    completedWriter?.HardwareAccelerated == true,
                    WatermarkBurnedIn: true,
                    runtime?.Width ?? pipeline.Profile.Width,
                    runtime?.Height ?? pipeline.Profile.Height,
                    pipeline.Profile.FramesPerSecond,
                    endedAt - session.StartedAt,
                    integrity,
                    finalization.FailureReason,
                    session.StartedAt,
                    endedAt)));
            }

            var compositePath = RecordingFileNameService.GetAvailableMediaPath(
                primaryPath,
                RecordMediaRole.Composite,
                "多机位",
                session.RecordId);
            var compositeRequested = !string.IsNullOrWhiteSpace(active.CompositeTemporaryPath);
            MediaFileFinalizationResult? compositeFinalization = null;
            if (compositeRequested)
            {
                compositeFinalization = active.CompositeWriter is null
                    ? MultiCameraMediaFinalizationPolicy.DescribeWriterFailure(
                        active.CompositeTemporaryPath,
                        compositePath,
                        "多机位合成",
                        "多机位合成编码器启动或运行失败")
                    : MultiCameraMediaFinalizationPolicy.PromoteCompletedFile(
                        active.CompositeTemporaryPath,
                        compositePath,
                        required: false,
                        displayName: "多机位合成");
                var compositeIntegrity = compositeFinalization.Integrity == MediaIntegrityStatus.Complete &&
                                         (assets.Any(asset => asset.Integrity != MediaIntegrityStatus.Complete) || gaps.Count > 0)
                    ? MediaIntegrityStatus.Partial
                    : compositeFinalization.Integrity;
                assets.Add(MultiCameraMediaAssetFactory.Create(new RecordMediaAssetSpecification(
                    session.RecordId,
                    "composite",
                    "多机位合成",
                    RecordMediaRole.Composite,
                    compositeFinalization.AssetPath,
                    active.StorageAllocation,
                    active.CompositeWriter?.Codec ?? "H.264",
                    active.CompositeWriter?.EncoderName ?? string.Empty,
                    active.CompositeWriter?.HardwareAccelerated == true,
                    WatermarkBurnedIn: true,
                    _rig.CompositeWidth,
                    _rig.CompositeHeight,
                    _rig.CompositeFramesPerSecond,
                    endedAt - session.StartedAt,
                    compositeIntegrity,
                    compositeFinalization.FailureReason,
                    session.StartedAt,
                    endedAt)));
            }
            var overall = MultiCameraMediaFinalizationPolicy.DetermineOverallIntegrity(assets, gaps);
            foreach (var gap in gaps)
            {
                gap.RecordId = session.RecordId;
                gap.MediaAssetId = assets.FirstOrDefault(asset => asset.CameraId == gap.CameraId)?.Id;
            }
            var defaultVideoPath = compositeFinalization?.PromotedToFinalPath == true
                ? compositeFinalization.AssetPath
                : primaryPath;
            return new RecordingCompletion(primaryPath, endedAt, defaultVideoPath, assets, gaps, overall);
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
            if (storageLease is not null)
            {
                await storageLease.ReleaseAsync(CancellationToken.None);
            }
            _gate.Release();
        }
    }

    public async Task AbortAsync(RecordingSession session, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        RecordingStorageLease? storageLease = null;
        try
        {
            if (_active?.Session.RecordId != session.RecordId) return;
            var active = _active;
            storageLease = active.StorageLease;
            active.Cancellation.Cancel();
            try
            {
                await WaitForWriterLoopAsync(active);
            }
            finally
            {
                // Abort is a terminal cleanup path even when an isolated writer reports
                // an unexpected fault while the other routes are winding down.
                FinalizeWriters(active);
                foreach (var path in active.TemporaryPaths.Values) TryDelete(path);
                TryDelete(active.CompositeTemporaryPath);
                _active = null;
            }
        }
        finally
        {
            if (storageLease is not null)
            {
                await storageLease.ReleaseAsync(CancellationToken.None);
            }
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

    public Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.FirstOrDefault(item => item.Profile.Id == _selectedCameraId)
            ?? _pipelines.Single(item => item.Profile.IsPrimary);
        return Task.FromResult(SavePipelineSnapshot(pipeline, cancellationToken));
    }

    public Task<IReadOnlyList<string>> TakeAllSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        foreach (var pipeline in _pipelines.Where(item => item.State == CameraConnectionState.Connected))
        {
            paths.Add(SavePipelineSnapshot(pipeline, cancellationToken));
        }
        return Task.FromResult<IReadOnlyList<string>>(paths);
    }

    private string SavePipelineSnapshot(Pipeline pipeline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mat snapshot;
        lock (pipeline.FrameSync)
        {
            if (pipeline.LatestFrame is null || pipeline.LatestFrame.Empty())
            {
                throw new InvalidOperationException($"机位“{pipeline.Profile.DisplayName}”暂无可拍摄画面。");
            }
            snapshot = pipeline.LatestFrame.Clone();
        }
        using (snapshot)
        {
            var root = _active?.StorageAllocation.RootPath ??
                       _storagePoolOptions.Targets
                           .Where(target => target.Enabled)
                           .OrderBy(target => target.Priority)
                           .Select(target => target.RootPath)
                           .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ??
                       _storageOptions.RecordingRoot;
            var directory = Path.Combine(root, "Snapshots", DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(directory);
            var tracking = RecordingFileNameService.SanitizePart(_active?.Session.TrackingNo ?? "snapshot");
            var camera = RecordingFileNameService.SanitizePart(pipeline.Profile.DisplayName);
            var path = Path.Combine(directory, $"{tracking}_{camera}_{DateTime.Now:yyyyMMddHHmmssfff}.jpg");
            if (!Cv2.ImWrite(path, snapshot))
            {
                throw new IOException("照片保存失败。");
            }
            return path;
        }
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
        await StartPreviewCoreAsync(enforcePerformanceAdmission: false, cancellationToken);
        await ReportTimedStageAsync(
            progress,
            "正在预热摄像头",
            startPercent: 5,
            endPercent: 13,
            durationSeconds: 5,
            cancellationToken);
        var starts = _pipelines.ToDictionary(
            pipeline => pipeline.Profile.Id,
            pipeline => (Captured: pipeline.CapturedFrames, Dropped: pipeline.DroppedFrames));
        var enabledStorageTargets = _storagePoolOptions.Targets
            .Where(target => target.Enabled)
            .OrderBy(target => target.Priority)
            .ToArray();
        if (enabledStorageTargets.Length == 0)
        {
            enabledStorageTargets = StoragePoolOptions.FromLegacyRecordingRoot(_storageOptions.RecordingRoot).Targets.ToArray();
        }
        const int probeBytes = 8 * 1024 * 1024;
        const int measuredSeconds = 55;
        var testFiles = new List<string>();
        var storageProbes = new List<StorageProbeMeasurement>();
        string? runDirectory = null;
        try
        {
            for (var index = 0; index < enabledStorageTargets.Length; index++)
            {
                var target = enabledStorageTargets[index];
                progress?.Report(new CameraPerformanceProgress(
                    14 + (int)Math.Round((index + 1d) / enabledStorageTargets.Length * 3),
                    $"正在检查录像盘“{target.DisplayName}”写入速度"));
                storageProbes.Add(await ProbeStorageTargetAsync(target, probeBytes, cancellationToken));
            }
            var writeTarget = storageProbes.FirstOrDefault(probe => probe.Result.Passed)
                ?? throw new IOException("所有启用录像盘位均无法完成写入测速。");
            runDirectory = Path.Combine(writeTarget.TestDirectory, $"run-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runDirectory);

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuAtStart = process.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();
            var encodingTasks = _pipelines
                .Select(pipeline => RunEncodingProbeAsync(pipeline, runDirectory, measuredSeconds, cancellationToken))
                .ToList();
            if (_rig.EffectiveCompositeRecordingEnabled)
            {
                encodingTasks.Add(RunCompositeEncodingProbeAsync(runDirectory, measuredSeconds, cancellationToken));
            }
            var progressTask = ReportTimedStageAsync(
                progress,
                "正在实际采集、烧录水印、编码并写盘",
                startPercent: 18,
                endPercent: 94,
                durationSeconds: measuredSeconds,
                cancellationToken);
            await Task.WhenAll(encodingTasks.Cast<Task>().Append(progressTask));
            var encodingResults = encodingTasks.Select(task => task.Result).ToArray();
            testFiles.AddRange(encodingResults.Select(result => result.FilePath));
            stopwatch.Stop();
            process.Refresh();
            var cpuPercent = (process.TotalProcessorTime - cpuAtStart).TotalMilliseconds /
                             Math.Max(1, stopwatch.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100d;
            progress?.Report(new CameraPerformanceProgress(97, "正在分析测试结果"));
            var cameraResults = _pipelines.Select(pipeline =>
            {
                var captureFrames = pipeline.CapturedFrames - starts[pipeline.Profile.Id].Captured;
                var droppedFrames = pipeline.DroppedFrames - starts[pipeline.Profile.Id].Dropped;
                var captureFps = captureFrames / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
                var encoding = encodingResults.Single(result => result.CameraId == pipeline.Profile.Id);
                var encodingFps = encoding.WrittenFrames / Math.Max(0.1, encoding.Elapsed.TotalSeconds);
                var actual = Math.Min(captureFps, encodingFps);
                var target = pipeline.Profile.FramesPerSecond;
                var dropped = (droppedFrames + encoding.MissingFrames) /
                              (double)Math.Max(1, captureFrames + droppedFrames + encoding.MissingFrames);
                var passed = encoding.Error is null &&
                             pipeline.State == CameraConnectionState.Connected &&
                             actual >= target * 0.9 &&
                             dropped < 0.01;
                return new CameraPerformanceResult(
                    pipeline.Profile.Id,
                    pipeline.Profile.DisplayName,
                    passed,
                    target,
                    actual,
                    dropped,
                    EstimateMegabits(pipeline.Profile),
                    passed
                        ? $"通过，采集 {captureFps:F1}fps / 编码 {encodingFps:F1}fps"
                        : encoding.Error ?? $"实际 {actual:F1}fps、丢帧 {dropped:P1}，请改用子码流或降低该机位分辨率/帧率");
            }).ToArray();
            var bitrate = cameraResults.Sum(item => item.EstimatedMegabitsPerSecond) +
                          (_rig.EffectiveCompositeRecordingEnabled ? 8 : 0);
            var megabytesPerHour = bitrate * 3600 / 8;
            var availableHours = storageProbes.Sum(item => item.AvailableBytes) * 0.75 /
                                 (megabytesPerHour * 1024 * 1024);
            var minimumRequiredWriteMegabytes = bitrate * 1.25 / 8;
            var storageResults = storageProbes.Select(item =>
            {
                var passed = item.Result.Passed &&
                             item.Result.WriteMegabytesPerSecond >= minimumRequiredWriteMegabytes;
                return item.Result with
                {
                    Passed = passed,
                    Message = passed
                        ? item.Result.Message
                        : item.Result.Passed
                            ? $"写入 {item.Result.WriteMegabytesPerSecond:F1}MB/s，低于当前方案所需 {minimumRequiredWriteMegabytes:F1}MB/s"
                            : item.Result.Message
                };
            }).ToArray();
            var diskPassed = storageResults.All(item => item.Passed);
            var cpuPassed = cpuPercent <= 75;
            var compositeResult = encodingResults.FirstOrDefault(item => item.CameraId == "composite");
            var compositePassed = compositeResult is null ||
                                  (compositeResult.Error is null &&
                                   compositeResult.WrittenFrames / Math.Max(0.1, compositeResult.Elapsed.TotalSeconds) >=
                                   _rig.CompositeFramesPerSecond * 0.9);
            var passed = diskPassed && cpuPassed && compositePassed && cameraResults.All(item => item.Passed);
            var slowestWrite = storageProbes.Count == 0
                ? 0
                : storageProbes.Min(item => item.Result.WriteMegabytesPerSecond);
            var result = new CameraRigPerformanceResult(
                passed,
                cameraResults,
                megabytesPerHour,
                availableHours,
                passed
                    ? $"全部机位真实性能测试通过，CPU {cpuPercent:F0}%"
                    : $"性能余量不足（CPU {cpuPercent:F0}% / 最慢盘位 {slowestWrite:F1}MB/s）；请优先改用子码流、降低副机位画质或减少机位")
            {
                ConfigurationFingerprint = CameraRigPerformanceAdmission.ComputeFingerprint(_rig, _storagePoolOptions),
                StorageTargets = storageResults
            };
            progress?.Report(new CameraPerformanceProgress(100, passed ? "测试完成，配置可用" : "测试完成，请查看改进建议"));
            return result;
        }
        finally
        {
            foreach (var testFile in testFiles)
            {
                TryDelete(testFile);
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(runDirectory) && Directory.Exists(runDirectory))
                {
                    foreach (var remainingFile in Directory.EnumerateFiles(runDirectory))
                    {
                        TryDelete(remainingFile);
                    }
                    Directory.Delete(runDirectory, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<StorageProbeMeasurement> ProbeStorageTargetAsync(
        StorageTarget target,
        int probeBytes,
        CancellationToken cancellationToken)
    {
        var testDirectory = Path.Combine(target.RootPath, ".unpackvision", "performance");
        var probePath = Path.Combine(testDirectory, $"write-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(testDirectory);
            var stopwatch = Stopwatch.StartNew();
            await using (var stream = new FileStream(
                             probePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(new byte[probeBytes], cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            stopwatch.Stop();
            var speed = probeBytes / 1024d / 1024d / Math.Max(0.01, stopwatch.Elapsed.TotalSeconds);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target.RootPath))!);
            return new StorageProbeMeasurement(
                new StoragePerformanceResult(target.Id, target.DisplayName, true, speed, $"写入 {speed:F1}MB/s"),
                testDirectory,
                drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new StorageProbeMeasurement(
                new StoragePerformanceResult(target.Id, target.DisplayName, false, 0, ex.Message),
                testDirectory,
                0);
        }
        finally
        {
            TryDelete(probePath);
        }
    }

    private async Task<EncodingProbeResult> RunEncodingProbeAsync(
        Pipeline pipeline,
        string testDirectory,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var runtime = pipeline.Backend.RuntimeInfo;
        var width = runtime?.Width > 0 ? runtime.Width : pipeline.Profile.Width;
        var height = runtime?.Height > 0 ? runtime.Height : pipeline.Profile.Height;
        if (pipeline.Profile.RotationQuarterTurns % 2 != 0)
        {
            (width, height) = (height, width);
        }
        var size = new Size(Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));
        var path = Path.Combine(testDirectory, $"camera-{pipeline.Profile.Id}-{Guid.NewGuid():N}.mp4");
        var startedAt = DateTimeOffset.Now;
        var session = new RecordingSession(Guid.Empty, "PERFORMANCE-TEST", WorkflowMode.Unpacking, startedAt, path);
        long written = 0;
        long missing = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var writer = OpenWriter(path, pipeline.Profile.Codec, pipeline.Profile.FramesPerSecond, size);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Math.Max(1, pipeline.Profile.FramesPerSecond)));
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds) &&
                   await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Mat frame;
                lock (pipeline.FrameSync)
                {
                    if (pipeline.LatestFrame is null || DateTimeOffset.Now - pipeline.LastFrameAt > TimeSpan.FromSeconds(1))
                    {
                        frame = CreatePlaceholder(size, pipeline.Profile.DisplayName);
                        missing++;
                    }
                    else
                    {
                        frame = pipeline.LatestFrame.Clone();
                    }
                }
                using (frame)
                using (var resized = ResizeTo(frame, size))
                {
                    RecordingOverlayRenderer.Draw(resized, session, []);
                    writer.Write(resized);
                    written++;
                }
            }
            return new EncodingProbeResult(pipeline.Profile.Id, written, missing, stopwatch.Elapsed, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EncodingProbeResult(pipeline.Profile.Id, written, missing, stopwatch.Elapsed, path, ex.Message);
        }
    }

    private async Task<EncodingProbeResult> RunCompositeEncodingProbeAsync(
        string testDirectory,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(testDirectory, $"composite-{Guid.NewGuid():N}.mp4");
        var outputSize = new Size(_rig.CompositeWidth, _rig.CompositeHeight);
        var startedAt = DateTimeOffset.Now;
        var session = new RecordingSession(Guid.Empty, "PERFORMANCE-TEST", WorkflowMode.Unpacking, startedAt, path);
        long written = 0;
        long missing = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var writer = OpenWriter(path, "mp4v", _rig.CompositeFramesPerSecond, outputSize);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / Math.Max(1, _rig.CompositeFramesPerSecond)));
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds) &&
                   await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var rendered = new List<(Pipeline Pipeline, Mat Frame)>(_pipelines.Count);
                try
                {
                    foreach (var pipeline in _pipelines)
                    {
                        Mat frame;
                        lock (pipeline.FrameSync)
                        {
                            if (pipeline.LatestFrame is null || DateTimeOffset.Now - pipeline.LastFrameAt > TimeSpan.FromSeconds(1))
                            {
                                frame = CreatePlaceholder(outputSize, pipeline.Profile.DisplayName);
                                missing++;
                            }
                            else
                            {
                                frame = pipeline.LatestFrame.Clone();
                            }
                        }
                        RecordingOverlayRenderer.Draw(frame, session, []);
                        rendered.Add((pipeline, frame));
                    }
                    using var composite = ComposeGrid(rendered, outputSize);
                    writer.Write(composite);
                    written++;
                }
                finally
                {
                    foreach (var item in rendered)
                    {
                        item.Frame.Dispose();
                    }
                }
            }
            return new EncodingProbeResult("composite", written, missing, stopwatch.Elapsed, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EncodingProbeResult("composite", written, missing, stopwatch.Elapsed, path, ex.Message);
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
                try { await WaitForWriterLoopAsync(active); } catch { }
                FinalizeWriters(active);
                await active.StorageLease.ReleaseAsync(CancellationToken.None);
                _active = null;
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

    /// <summary>
    /// Each encoder owns its own clock and writer. A slow secondary encoder can drop its
    /// own frames without delaying the remaining cameras, and finalization only starts
    /// after every owner loop has stopped.
    /// </summary>
    private async Task CameraWriterLoopAsync(ActiveRecording active, Pipeline pipeline)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(1d / Math.Max(1, pipeline.Profile.FramesPerSecond)));
        if (active.Writers[pipeline.Profile.Id] is { } activeWriter)
        {
            pipeline.EncodingMetrics.Begin(activeWriter);
        }
        else
        {
            pipeline.EncodingMetrics.End();
        }
        try
        {
            while (await timer.WaitForNextTickAsync(active.Cancellation.Token).ConfigureAwait(false))
            {
                var processingStartedAt = Stopwatch.GetTimestamp();
                var now = DateTimeOffset.Now;
                Mat frame;
                bool missing;
                lock (pipeline.FrameSync)
                {
                    missing = pipeline.LatestFrame is null ||
                              now - pipeline.LastFrameAt > TimeSpan.FromSeconds(1);
                    frame = missing
                        ? CreatePlaceholder(active.OutputSizes[pipeline.Profile.Id], pipeline.Profile.DisplayName)
                        : pipeline.LatestFrame!.Clone();
                }

                if (missing) BeginGap(active, pipeline, now);
                else EndGap(active, pipeline, now);

                using (frame)
                using (var resized = ResizeTo(frame, active.OutputSizes[pipeline.Profile.Id]))
                {
                    RecordingOverlayRenderer.Draw(resized, active.Session, active.IssueTags);
                    if (active.Writers[pipeline.Profile.Id] is not { } writer)
                    {
                        continue;
                    }
                    try
                    {
                        writer.Write(resized);
                        pipeline.WrittenFrames++;
                        pipeline.EncodingMetrics.RecordWrite(Stopwatch.GetElapsedTime(processingStartedAt));
                    }
                    catch (Exception ex)
                    {
                        writer.Dispose();
                        active.Writers[pipeline.Profile.Id] = null;
                        pipeline.EncodingMetrics.End();
                        pipeline.LastError = ex.Message;
                        pipeline.State = CameraConnectionState.Failed;
                        PublishState(pipeline);
                        if (pipeline.Profile.IsPrimary)
                        {
                            ReportPrimaryRecordingFailure(
                                active,
                                pipeline,
                                ex,
                                PrimaryRecordingFailureKind.WriterFailure,
                                $"主机位“{pipeline.Profile.DisplayName}”录像写入失败");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            pipeline.EncodingMetrics.End();
        }
    }

    /// <summary>
    /// Composite work is deliberately independent and lower priority than evidence streams.
    /// Five-or-more-camera rigs do not create this loop unless the operator explicitly enables it.
    /// </summary>
    private async Task CompositeWriterLoopAsync(ActiveRecording active)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(1d / Math.Max(1, _rig.CompositeFramesPerSecond)));
        try
        {
            while (await timer.WaitForNextTickAsync(active.Cancellation.Token).ConfigureAwait(false))
            {
                var rendered = new List<(Pipeline Pipeline, Mat Frame)>(_pipelines.Count);
                try
                {
                    var now = DateTimeOffset.Now;
                    foreach (var pipeline in _pipelines)
                    {
                        Mat frame;
                        lock (pipeline.FrameSync)
                        {
                            frame = pipeline.LatestFrame is null ||
                                    now - pipeline.LastFrameAt > TimeSpan.FromSeconds(1)
                                ? CreatePlaceholder(active.OutputSizes[pipeline.Profile.Id], pipeline.Profile.DisplayName)
                                : pipeline.LatestFrame.Clone();
                        }
                        RecordingOverlayRenderer.Draw(frame, active.Session, active.IssueTags);
                        rendered.Add((pipeline, frame));
                    }

                    using var composite = ComposeGrid(
                        rendered,
                        new Size(_rig.CompositeWidth, _rig.CompositeHeight));
                    if (active.CompositeWriter is not { } writer)
                    {
                        return;
                    }
                    try
                    {
                        writer.Write(composite);
                    }
                    catch
                    {
                        writer.Dispose();
                        active.CompositeWriter = null;
                        return;
                    }
                }
                finally
                {
                    foreach (var item in rendered)
                    {
                        item.Frame.Dispose();
                    }
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
            var failure = new IOException($"主机位“{pipeline.Profile.DisplayName}”画面中断超过5秒");
            ReportPrimaryRecordingFailure(
                active,
                pipeline,
                failure,
                PrimaryRecordingFailureKind.FrameGap,
                failure.Message);
        }
    }

    private void ReportPrimaryRecordingFailure(
        ActiveRecording active,
        Pipeline pipeline,
        Exception failure,
        PrimaryRecordingFailureKind kind,
        string message)
    {
        var recordingIsStopping = active.Cancellation.IsCancellationRequested;
        if (recordingIsStopping ||
            Interlocked.CompareExchange(ref active.PrimaryFailure, failure, null) is not null ||
            !active.PrimaryFailureNotification.TryClaim(recordingIsStopping))
        {
            return;
        }

        try
        {
            PrimaryRecordingFailed?.Invoke(this, new PrimaryRecordingFailureEventArgs(
                active.Session.RecordId,
                pipeline.Profile.Id,
                pipeline.Profile.DisplayName,
                kind,
                message,
                failure));
        }
        catch
        {
            // A UI subscriber must never keep a failed evidence writer alive.
        }
    }

    private static void EndGap(ActiveRecording active, Pipeline pipeline, DateTimeOffset now)
    {
        if (pipeline.GapStartedAt is not { } started) return;
        active.Gaps.Enqueue(new MediaGap
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
        var (columns, rows) = MultiCameraRecordingPolicy.GetCompositeGridDimensions(frames.Count);
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

    private IRecordingFrameWriter OpenWriter(string path, string codecName, double fps, Size size)
    {
        if (_gstreamerLaunchPath is not null && _gstreamerEncoder is not null)
        {
            return new GStreamerRecordingFrameWriter(
                _gstreamerLaunchPath,
                _gstreamerEncoder,
                path,
                fps,
                size);
        }
        return new OpenCvRecordingFrameWriter(path, codecName, fps, size);
    }

    private void FinalizeWriters(ActiveRecording active)
    {
        if (active.WriterShutdownTimedOut)
        {
            foreach (var key in active.Writers.Keys.ToArray()) active.Writers[key] = null;
            active.CompositeWriter = null;
            active.Cancellation.Dispose();
            return;
        }
        foreach (var pair in active.Writers.ToArray())
        {
            if (pair.Value is null) continue;
            try
            {
                pair.Value.Complete();
            }
            catch (Exception ex)
            {
                active.Writers[pair.Key] = null;
                var pipeline = _pipelines.First(item => item.Profile.Id == pair.Key);
                pipeline.LastError = ex.Message;
                pipeline.State = CameraConnectionState.Failed;
                if (pipeline.Profile.IsPrimary)
                {
                    Interlocked.CompareExchange(ref active.PrimaryFailure, ex, null);
                }
                PublishState(pipeline);
            }
        }
        if (active.CompositeWriter is not null)
        {
            try
            {
                active.CompositeWriter.Complete();
            }
            catch
            {
                active.CompositeWriter = null;
            }
        }
        active.Cancellation.Dispose();
    }

    private async Task WaitForWriterLoopAsync(ActiveRecording active)
    {
        var completed = await IndependentWriterLoopGroup.WaitForShutdownAsync(
            active.WriterLoop,
            WriterLoopShutdownTimeout,
            () => ForceTerminateWriters(active),
            WriterForceTerminationGrace).ConfigureAwait(false);
        if (!completed)
        {
            active.WriterShutdownTimedOut = true;
            Interlocked.CompareExchange(
                ref active.PrimaryFailure,
                new TimeoutException("录像编码线程未在安全时限内停止，编码器已被强制终止。"),
                null);
        }
    }

    private static void ForceTerminateWriters(ActiveRecording active)
    {
        foreach (var writer in active.Writers.Values.Where(writer => writer is not null))
        {
            _ = Task.Run(() =>
            {
                try { writer!.Abort(); } catch { }
            });
        }
        if (active.CompositeWriter is { } compositeWriter)
        {
            _ = Task.Run(() =>
            {
                try { compositeWriter.Abort(); } catch { }
            });
        }
    }

    private async Task EnsureProductionMediaRuntimeAsync(CancellationToken cancellationToken)
    {
        if (_gstreamerLaunchPath is not null && _gstreamerEncoder is not null) return;
        var capabilities = await _mediaRuntimeProbe.ProbeAsync(cancellationToken);
        var selection = MultiCameraRecordingPolicy.RequireProductionRuntime(capabilities);
        _gstreamerLaunchPath = selection.LaunchExecutablePath;
        _gstreamerEncoder = selection.Encoder;
    }

    private Pipeline Selected() => _pipelines.FirstOrDefault(pipeline => pipeline.Profile.Id == _selectedCameraId)
        ?? _pipelines.Single(pipeline => pipeline.Profile.IsPrimary);

    private ActiveRecording EnsureActive(RecordingSession session) =>
        _active?.Session.RecordId == session.RecordId ? _active : throw new InvalidOperationException("录像会话已经失效");

    private CameraRuntimeState ToRuntimeState(Pipeline pipeline)
    {
        var runtime = pipeline.Backend.RuntimeInfo;
        var encoding = pipeline.EncodingMetrics.Snapshot();
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
            pipeline.LastError,
            encoding.FramesPerSecond,
            encoding.QueueDepth,
            encoding.ProcessingLatencyMilliseconds,
            encoding.EncoderName,
            encoding.HardwareAccelerated);
    }

    private void PublishState(Pipeline pipeline)
    {
        try { CameraStateChanged?.Invoke(this, ToRuntimeState(pipeline)); } catch { }
    }

    private static CameraRigOptions NormalizeRig(CameraRigOptions rig)
    {
        if (rig.EnabledCameras.Count == 0) throw new InvalidOperationException("至少需要启用一个机位");
        if (rig.EnabledCameras.Count > CameraRigOptions.MaximumEnabledCameras)
            throw new InvalidOperationException($"最多只能启用{CameraRigOptions.MaximumEnabledCameras}个机位");
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

    private long EstimateBytesPerHour(IEnumerable<CameraProfile> profiles)
    {
        var megabitsPerSecond = profiles.Sum(EstimateMegabits) +
                                (_rig.EffectiveCompositeRecordingEnabled ? 8 : 0);
        var bytes = megabitsPerSecond * 1_000_000d / 8d * 3600d;
        return bytes >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)Math.Ceiling(bytes));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
