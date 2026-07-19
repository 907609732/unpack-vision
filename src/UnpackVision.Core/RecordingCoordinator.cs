namespace UnpackVision.Core;

public enum ScanAction
{
    Started,
    Stopped,
    StopIgnored,
    Busy,
    Invalid,
    Failed
}

public sealed record ScanResult(ScanAction Action, string Message, ScanRecord? Record = null);

public sealed class RecordingCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IScanRecordRepository _repository;
    private readonly IRecordingBackend _recordingBackend;
    private readonly IEventPublisher _eventPublisher;
    private readonly IClock _clock;
    private readonly string _connectorId;
    private RecordingSession? _session;
    private ScanRecord? _currentRecord;

    public RecordingCoordinator(
        IScanRecordRepository repository,
        IRecordingBackend recordingBackend,
        IEventPublisher eventPublisher,
        IClock clock,
        ScannerProfile scannerProfile,
        string connectorId = "excel")
    {
        _repository = repository;
        _recordingBackend = recordingBackend;
        _eventPublisher = eventPublisher;
        _clock = clock;
        ScannerProfile = scannerProfile;
        _connectorId = connectorId;
    }

    public ScannerProfile ScannerProfile { get; private set; }
    public RecordingState State => _currentRecord?.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Saving
        ? _currentRecord.State
        : RecordingState.Idle;
    public ScanRecord? CurrentRecord => _currentRecord;
    public event EventHandler<ScanResult>? StateChanged;

    public void UpdateScannerProfile(ScannerProfile scannerProfile) => ScannerProfile = scannerProfile;

    public async Task<ScanResult> ProcessScanAsync(
        string rawValue,
        WorkflowMode workflow,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var value = ScannerProfile.Normalize(rawValue);
            var trackingValue = ScannerProfile.PrepareTrackingNumber(value);
            var validationError = ScannerProfile.ValidateTrackingNumber(trackingValue);
            if (validationError is not null)
            {
                return Notify(new ScanResult(ScanAction.Invalid, validationError));
            }

            if (_session is not null)
            {
                var comparison = ScannerProfile.CaseSensitive
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                if (string.Equals(_currentRecord!.TrackingNo, trackingValue, comparison))
                {
                    return await StopInternalAsync(cancellationToken);
                }

                var stopped = await StopInternalAsync(cancellationToken);
                if (stopped.Action != ScanAction.Stopped ||
                    !stopped.Message.Contains("已保存", StringComparison.Ordinal))
                {
                    return stopped;
                }
                return await StartInternalAsync(trackingValue, workflow, cancellationToken, switchedFromPrevious: true);
            }

            return await StartInternalAsync(trackingValue, workflow, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScanResult> StartInternalAsync(
        string trackingValue,
        WorkflowMode workflow,
        CancellationToken cancellationToken,
        bool switchedFromPrevious = false)
    {
        var now = _clock.Now;
        var duplicate = await _repository.FindFirstCompletedAsync(trackingValue, cancellationToken);
        var record = new ScanRecord
        {
            TrackingNo = trackingValue,
            Workflow = workflow,
            State = RecordingState.Starting,
            ScannedAt = now,
            DuplicateOf = duplicate?.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        _currentRecord = record;
        await _repository.AddAsync(record, cancellationToken);

        try
        {
            _session = await _recordingBackend.StartAsync(
                record.Id,
                record.TrackingNo,
                record.Workflow,
                record.ScannedAt,
                cancellationToken);
            record.State = RecordingState.Recording;
            record.RecordingStartedAt = _session.StartedAt;
            record.VideoPath = _session.TemporaryPath;
            record.UpdatedAt = _clock.Now;
            await _repository.UpdateAsync(record, cancellationToken);
            await _eventPublisher.PublishAsync(
                duplicate is null ? "record.started" : "record.duplicate",
                record,
                cancellationToken);
            var prefix = switchedFromPrevious ? "上一单录像已保存；" : string.Empty;
            var suffix = duplicate is null ? string.Empty : "；检测到重复单号";
            return Notify(new ScanResult(ScanAction.Started, $"{prefix}已开始录制{suffix}", record));
        }
        catch (Exception ex)
        {
            record.State = RecordingState.Failed;
            record.FailureReason = ex.Message;
            record.UpdatedAt = _clock.Now;
            await _repository.UpdateAsync(record, cancellationToken);
            await _eventPublisher.PublishAsync("record.failed", record, cancellationToken);
            _session = null;
            _currentRecord = null;
            return Notify(new ScanResult(ScanAction.Failed, $"启动录像失败：{ex.Message}", record));
        }
    }

    public async Task<ScanResult> EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _session is null
                ? Notify(new ScanResult(ScanAction.StopIgnored, "当前没有正在录制的视频"))
                : await StopInternalAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScanResult> StopInternalAsync(CancellationToken cancellationToken)
    {
        var session = _session!;
        var record = _currentRecord!;
        record.State = RecordingState.Saving;
        record.UpdatedAt = _clock.Now;
        await _repository.UpdateAsync(record, cancellationToken);
        Notify(new ScanResult(ScanAction.Stopped, "正在保存录像…", record));

        try
        {
            var completion = await _recordingBackend.StopAsync(session, cancellationToken);
            record.State = RecordingState.Completed;
            record.RecordingEndedAt = completion.EndedAt;
            record.VideoPath = completion.VideoPath;
            record.UpdatedAt = _clock.Now;
            await _repository.CompleteAndEnqueueAsync(record, _connectorId, cancellationToken);
            await _eventPublisher.PublishAsync("record.completed", record, cancellationToken);
            return Notify(new ScanResult(ScanAction.Stopped, "录像已保存", record));
        }
        catch (Exception ex)
        {
            record.State = RecordingState.Failed;
            record.FailureReason = ex.Message;
            record.UpdatedAt = _clock.Now;
            await _repository.UpdateAsync(record, cancellationToken);
            await _eventPublisher.PublishAsync("record.failed", record, cancellationToken);
            return Notify(new ScanResult(ScanAction.Failed, $"保存录像失败：{ex.Message}", record));
        }
        finally
        {
            _session = null;
            _currentRecord = null;
        }
    }

    private ScanResult Notify(ScanResult result)
    {
        StateChanged?.Invoke(this, result);
        return result;
    }
}
