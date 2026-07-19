using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class InterruptedRecordingRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionRecovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task ActiveRecordIsMarkedFailedAndTemporaryFileIsPreserved()
    {
        Directory.CreateDirectory(_root);
        var temporaryPath = Path.Combine(_root, "record.partial.mp4");
        await File.WriteAllBytesAsync(temporaryPath, [1, 2, 3]);
        var repository = new InMemoryRepository();
        var record = new ScanRecord
        {
            TrackingNo = "00123-ABC",
            State = RecordingState.Recording,
            ScannedAt = DateTimeOffset.Now,
            VideoPath = temporaryPath,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        repository.Records.Add(record);
        var recovery = new InterruptedRecordingRecovery(repository, new FakeClock(DateTimeOffset.Now));

        var count = await recovery.MarkInterruptedAsync();

        Assert.Equal(1, count);
        Assert.Equal(RecordingState.Failed, record.State);
        Assert.Contains("临时文件已保留", record.FailureReason);
        Assert.True(File.Exists(temporaryPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        GC.SuppressFinalize(this);
    }
}
