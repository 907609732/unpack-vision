using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class HikRecordingImporter
{
    private readonly IScanRecordRepository _repository;
    private readonly IClock _clock;

    public HikRecordingImporter(IScanRecordRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<ScanRecord?> ImportAsync(
        string path,
        WorkflowMode workflow,
        bool enqueueSync,
        CancellationToken cancellationToken = default)
    {
        if (!HikRecordingFileParser.TryParse(path, workflow, out var parsed) || parsed is null)
        {
            return null;
        }
        if (await _repository.FindByVideoPathAsync(parsed.VideoPath, cancellationToken) is not null)
        {
            return null;
        }

        var duplicate = await _repository.FindFirstCompletedAsync(parsed.TrackingNo, cancellationToken);
        var now = _clock.Now;
        var record = new ScanRecord
        {
            TrackingNo = parsed.TrackingNo,
            Workflow = parsed.Workflow,
            State = RecordingState.Imported,
            ScannedAt = parsed.StartedAt,
            RecordingStartedAt = parsed.StartedAt,
            RecordingEndedAt = parsed.EndedAt,
            VideoPath = parsed.VideoPath,
            DuplicateOf = duplicate?.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _repository.AddImportedAsync(record, enqueueSync ? "excel" : null, cancellationToken);
        return record;
    }
}
