using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class InterruptedRecordingRecovery
{
    private readonly IScanRecordRepository _repository;
    private readonly IClock _clock;

    public InterruptedRecordingRecovery(IScanRecordRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var records = await _repository.QueryAsync(limit: 10_000, cancellationToken: cancellationToken);
        var interrupted = records.Where(record =>
            record.State is RecordingState.Starting or RecordingState.Recording or RecordingState.Saving).ToArray();

        foreach (var record in interrupted)
        {
            record.State = RecordingState.Failed;
            record.FailureReason = string.IsNullOrWhiteSpace(record.VideoPath)
                ? "程序上次运行时录像被中断，未找到临时录像路径"
                : File.Exists(record.VideoPath)
                    ? $"程序上次运行时录像被中断；临时文件已保留：{record.VideoPath}"
                    : $"程序上次运行时录像被中断；临时文件不存在：{record.VideoPath}";
            record.UpdatedAt = _clock.Now;
            await _repository.UpdateAsync(record, cancellationToken);
        }

        return interrupted.Length;
    }
}
