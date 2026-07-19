using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class RecordingCoordinatorTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"UnpackVisionTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ScanningCurrentTrackingAgainStopsAndSaves()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 19, 8, 0, 0, TimeSpan.FromHours(8)));
        var coordinator = new RecordingCoordinator(
            repository,
            backend,
            new NullEventPublisher(),
            clock,
            new ScannerProfile());

        var started = await coordinator.ProcessScanAsync("SF1234567890\r\n", WorkflowMode.Unpacking);
        var temporaryPathWhileRecording = started.Record?.VideoPath;
        var stopped = await coordinator.ProcessScanAsync("SF1234567890\r", WorkflowMode.Unpacking);

        Assert.Equal(ScanAction.Started, started.Action);
        Assert.EndsWith(".partial.mp4", temporaryPathWhileRecording, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ScanAction.Stopped, stopped.Action);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(1, backend.StopCount);
        Assert.Single(repository.Deliveries);
        Assert.Equal(RecordingState.Completed, repository.Records.Single().State);
        Assert.True(File.Exists(repository.Records.Single().VideoPath));
    }

    [Fact]
    public async Task ScanningDifferentTrackingStopsCurrentAndStartsNext()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var coordinator = new RecordingCoordinator(
            repository,
            backend,
            new NullEventPublisher(),
            new FakeClock(DateTimeOffset.Now),
            new ScannerProfile());

        var first = await coordinator.ProcessScanAsync("SF1234567890", WorkflowMode.Unpacking);
        var switched = await coordinator.ProcessScanAsync("YT9876543210", WorkflowMode.Unpacking);
        var stopped = await coordinator.ProcessScanAsync("YT9876543210", WorkflowMode.Unpacking);

        Assert.Equal(ScanAction.Started, first.Action);
        Assert.Equal(ScanAction.Started, switched.Action);
        Assert.Contains("上一单录像已保存", switched.Message, StringComparison.Ordinal);
        Assert.Equal("YT9876543210", switched.Record?.TrackingNo);
        Assert.Equal(ScanAction.Stopped, stopped.Action);
        Assert.Equal(2, backend.StartCount);
        Assert.Equal(2, backend.StopCount);
        Assert.Equal(2, repository.Records.Count);
        Assert.All(repository.Records, record => Assert.Equal(RecordingState.Completed, record.State));
        Assert.Equal(2, repository.Deliveries.Count);
    }

    [Fact]
    public async Task DuplicateTrackingIsRetainedAndMarked()
    {
        var repository = new InMemoryRepository();
        var existing = new ScanRecord
        {
            TrackingNo = "SF1234567890",
            State = RecordingState.Completed,
            ScannedAt = DateTimeOffset.Now.AddDays(-1),
            CreatedAt = DateTimeOffset.Now.AddDays(-1),
            UpdatedAt = DateTimeOffset.Now.AddDays(-1)
        };
        repository.Records.Add(existing);
        var coordinator = new RecordingCoordinator(
            repository,
            new FakeRecordingBackend(_temp),
            new NullEventPublisher(),
            new FakeClock(DateTimeOffset.Now),
            new ScannerProfile());

        var result = await coordinator.ProcessScanAsync(existing.TrackingNo, WorkflowMode.Unpacking);

        Assert.Equal(ScanAction.Started, result.Action);
        Assert.Equal(existing.Id, result.Record?.DuplicateOf);
    }

    [Fact]
    public async Task ConfiguredPrefixAndSuffixAreRemovedBeforeRecording()
    {
        var repository = new InMemoryRepository();
        var coordinator = new RecordingCoordinator(
            repository,
            new FakeRecordingBackend(_temp),
            new NullEventPublisher(),
            new FakeClock(DateTimeOffset.Now),
            new ScannerProfile
            {
                FilterPrefixEnabled = true,
                PrefixToRemove = "START-",
                FilterSuffixEnabled = true,
                SuffixToRemove = "-END"
            });

        var result = await coordinator.ProcessScanAsync("START-SF1234567890-END", WorkflowMode.Unpacking);

        Assert.Equal(ScanAction.Started, result.Action);
        Assert.Equal("SF1234567890", result.Record?.TrackingNo);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, true);
        }
        GC.SuppressFinalize(this);
    }
}
