using System.Collections.Concurrent;

namespace UnpackVision.Core;

public sealed class StationScanCommandRouter : IScanCommandRouter
{
    private readonly RecordingCoordinator _recordingCoordinator;
    private readonly IScanRecordRepository _repository;
    private readonly IClock _clock;
    private readonly ScannerProfile _scannerProfile;
    private readonly string _stationId;
    private readonly string _connectorId;
    private readonly IReadOnlyList<IssueTagDefinition> _issueTags;
    private readonly IScanCommandLedger? _commandLedger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ScanAcknowledgement>>> _commands =
        new(StringComparer.Ordinal);

    public StationScanCommandRouter(
        RecordingCoordinator recordingCoordinator,
        IScanRecordRepository repository,
        IClock clock,
        ScannerProfile scannerProfile,
        string? stationId = null,
        string connectorId = "excel",
        IReadOnlyList<IssueTagDefinition>? issueTags = null,
        IScanCommandLedger? commandLedger = null)
    {
        _recordingCoordinator = recordingCoordinator;
        _repository = repository;
        _clock = clock;
        _scannerProfile = scannerProfile;
        _stationId = string.IsNullOrWhiteSpace(stationId) ? Environment.MachineName : stationId.Trim();
        _connectorId = connectorId;
        _issueTags = issueTags ?? IssueTagDefaults.Create();
        _commandLedger = commandLedger;
    }

    public async Task<ScanAcknowledgement> RouteAsync(
        ScanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = command.EffectiveIdempotencyKey;
        var pending = _commands.GetOrAdd(
            key,
            _ => new Lazy<Task<ScanAcknowledgement>>(
                () => RoutePersistedAsync(key, command, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await pending.Value;
        }
        catch
        {
            _commands.TryRemove(new KeyValuePair<string, Lazy<Task<ScanAcknowledgement>>>(key, pending));
            throw;
        }
    }

    private async Task<ScanAcknowledgement> RoutePersistedAsync(
        string idempotencyKey,
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        if (_commandLedger is not null)
        {
            var saved = await _commandLedger.GetAsync(idempotencyKey, cancellationToken);
            if (saved is not null)
            {
                return saved;
            }
        }
        var acknowledgement = await RouteCoreAsync(command, cancellationToken);
        if (_commandLedger is not null)
        {
            await _commandLedger.SaveAsync(idempotencyKey, acknowledgement, cancellationToken);
        }
        return acknowledgement;
    }

    public StationStateSnapshot GetState()
    {
        var current = _recordingCoordinator.CurrentRecord;
        return new StationStateSnapshot(
            _stationId,
            _recordingCoordinator.State,
            current?.Id,
            current?.TrackingNo,
            _clock.Now);
    }

    private async Task<ScanAcknowledgement> RouteCoreAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.DeviceId))
        {
            return Reject(command, "缺少设备 ID");
        }

        return command.Mode == DeviceOperatingMode.ScanCollection ||
               command.Workflow == WorkflowMode.ScanCollection
            ? await CollectAsync(command, cancellationToken)
            : await RouteRecordingAsync(command, cancellationToken);
    }

    private async Task<ScanAcknowledgement> CollectAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        var trackingNo = _scannerProfile.PrepareTrackingNumber(command.Value);
        var validationError = _scannerProfile.ValidateTrackingNumber(trackingNo);
        if (validationError is not null)
        {
            return Reject(command, validationError, trackingNo);
        }

        var now = _clock.Now;
        var duplicate = await _repository.FindFirstCompletedAsync(trackingNo, cancellationToken);
        var record = new ScanRecord
        {
            TrackingNo = trackingNo,
            Workflow = WorkflowMode.ScanCollection,
            State = RecordingState.Collected,
            ScannedAt = command.DetectedAt == default ? now : command.DetectedAt,
            StationId = string.IsNullOrWhiteSpace(command.StationId) ? _stationId : command.StationId,
            DuplicateOf = duplicate?.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _repository.AddAsync(record, cancellationToken);
        await _repository.CompleteAndEnqueueAsync(record, _connectorId, cancellationToken);
        return new ScanAcknowledgement(
            command.EventId,
            ScanCommandAction.Collected,
            record.State,
            record.Id,
            record.TrackingNo,
            duplicate is null ? "单号已收集并进入 Excel 队列" : "单号已收集；检测到重复单号",
            now);
    }

    private async Task<ScanAcknowledgement> RouteRecordingAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Mode == DeviceOperatingMode.IssueRemote)
        {
            return await RouteIssueRemoteAsync(command, cancellationToken);
        }

        var result = await _recordingCoordinator.ProcessScanAsync(
            command.Value,
            command.Workflow,
            cancellationToken);
        var action = result.Action switch
        {
            ScanAction.Started when result.Message.Contains("上一单", StringComparison.Ordinal) =>
                ScanCommandAction.RecordingSwitched,
            ScanAction.Started => ScanCommandAction.RecordingStarted,
            ScanAction.Stopped => ScanCommandAction.RecordingStopped,
            ScanAction.StopIgnored or ScanAction.Busy => ScanCommandAction.Ignored,
            ScanAction.Invalid => ScanCommandAction.Rejected,
            _ => ScanCommandAction.Failed
        };
        return new ScanAcknowledgement(
            command.EventId,
            action,
            result.Record?.State ?? _recordingCoordinator.State,
            result.Record?.Id,
            result.Record?.TrackingNo,
            result.Message,
            _clock.Now);
    }

    private async Task<ScanAcknowledgement> RouteIssueRemoteAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        var record = _recordingCoordinator.CurrentRecord;
        if (_recordingCoordinator.State != RecordingState.Recording || record is null)
        {
            return Reject(command, "当前没有正在录像的包裹");
        }

        var value = (command.Value ?? string.Empty).Trim();
        var tagMatch = IssueTagBarcodeRouter.Match(value, _issueTags);
        if (tagMatch.Action == IssueBarcodeAction.AddTag && tagMatch.Tag is not null)
        {
            var taggedAt = command.DetectedAt == default ? _clock.Now : command.DetectedAt;
            var result = await _recordingCoordinator.AddIssueTagAsync(
                tagMatch.Tag,
                taggedAt,
                $"device:{command.DeviceId}",
                cancellationToken);
            return result is null
                ? Reject(command, "当前没有正在录像的包裹")
                : Accept(
                    command,
                    ScanCommandAction.IssueTagged,
                    result.AlreadyActive ? $"已经标记{tagMatch.Tag.Name}" : $"已标记{tagMatch.Tag.Name}");
        }
        if (tagMatch.Action == IssueBarcodeAction.UndoLastTag)
        {
            var removed = await _recordingCoordinator.UndoLastIssueTagAsync(cancellationToken);
            return removed is null
                ? Reject(command, "当前录像没有可撤销的异常标签")
                : Accept(command, ScanCommandAction.IssueUndone, $"已撤销{removed.TagName}");
        }
        if (string.Equals(value, IssueRemoteCommands.Stop, StringComparison.OrdinalIgnoreCase))
        {
            var stopped = await _recordingCoordinator.EmergencyStopAsync(cancellationToken);
            return new ScanAcknowledgement(
                command.EventId,
                stopped.Action == ScanAction.Stopped ? ScanCommandAction.RecordingStopped : ScanCommandAction.Failed,
                stopped.Record?.State ?? _recordingCoordinator.State,
                stopped.Record?.Id,
                stopped.Record?.TrackingNo,
                stopped.Message,
                _clock.Now);
        }
        if (string.Equals(value, IssueRemoteCommands.Snapshot, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _recordingCoordinator.TakeCurrentSnapshotAsync(cancellationToken);
            return snapshot is null
                ? Reject(command, "当前没有正在录像的包裹")
                : Accept(command, ScanCommandAction.SnapshotCaptured, "问题画面已截图");
        }
        if (value.StartsWith(IssueRemoteCommands.NotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var note = value[IssueRemoteCommands.NotePrefix.Length..].Trim();
            if (note.Length > 2000)
            {
                return Reject(command, "备注不能超过 2000 个字符");
            }
            var updated = await _recordingCoordinator.UpdateCurrentNoteAsync(note, cancellationToken);
            return updated
                ? Accept(command, ScanCommandAction.NoteUpdated, "备注已保存")
                : Reject(command, "当前没有正在录像的包裹");
        }
        return Reject(command, "无法识别异常遥控指令");
    }

    private ScanAcknowledgement Accept(ScanCommand command, ScanCommandAction action, string message)
    {
        var current = _recordingCoordinator.CurrentRecord;
        return new ScanAcknowledgement(
            command.EventId,
            action,
            current?.State ?? _recordingCoordinator.State,
            current?.Id,
            current?.TrackingNo,
            message,
            _clock.Now);
    }

    private ScanAcknowledgement Reject(ScanCommand command, string message, string? trackingNo = null) =>
        new(
            command.EventId,
            ScanCommandAction.Rejected,
            _recordingCoordinator.State,
            _recordingCoordinator.CurrentRecord?.Id,
            trackingNo ?? _recordingCoordinator.CurrentRecord?.TrackingNo,
            message,
            _clock.Now);
}
