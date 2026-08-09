using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class StationScanCommandRouterTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"UnpackVisionStation-{Guid.NewGuid():N}");

    [Fact]
    public async Task CollectionCommandIsPersistedAndQueuedWithoutRecording()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.FromHours(8)));
        var profile = new ScannerProfile();
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile, "station-a");
        var command = new ScanCommand
        {
            EventId = Guid.NewGuid(),
            DeviceId = "phone-a",
            StationId = "station-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.ScanCollection,
            Workflow = WorkflowMode.ScanCollection,
            DetectedAt = clock.Now
        };

        var acknowledgement = await router.RouteAsync(command);

        Assert.Equal(ScanCommandAction.Collected, acknowledgement.Action);
        Assert.Equal(RecordingState.Collected, acknowledgement.State);
        Assert.Equal(0, backend.StartCount);
        Assert.Single(repository.Records);
        Assert.Single(repository.Deliveries);
        Assert.Null(repository.Records[0].VideoPath);
    }

    [Fact]
    public async Task RepeatedEventIsIdempotent()
    {
        var repository = new InMemoryRepository();
        var profile = new ScannerProfile();
        var clock = new FakeClock(DateTimeOffset.Now);
        var coordinator = new RecordingCoordinator(
            repository,
            new FakeRecordingBackend(_temp),
            new NullEventPublisher(),
            clock,
            profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);
        var command = new ScanCommand
        {
            EventId = Guid.NewGuid(),
            IdempotencyKey = "stable-event-1",
            DeviceId = "phone-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.ScanCollection
        };

        var first = await router.RouteAsync(command);
        var second = await router.RouteAsync(command with { EventId = Guid.NewGuid() });

        Assert.Equal(first.RecordId, second.RecordId);
        Assert.Single(repository.Records);
        Assert.Single(repository.Deliveries);
    }

    [Fact]
    public async Task HandheldCommandUsesExistingRecordingCoordinator()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var profile = new ScannerProfile();
        var clock = new FakeClock(DateTimeOffset.Now);
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);

        var acknowledgement = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.HandheldScanner,
            Workflow = WorkflowMode.Unpacking
        });

        Assert.Equal(ScanCommandAction.RecordingStarted, acknowledgement.Action);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(RecordingState.Recording, router.GetState().RecordingState);
    }

    [Fact]
    public async Task IssueRemoteCanTagSnapshotAndUndoCurrentRecording()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var profile = new ScannerProfile();
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.FromHours(8)));
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);
        await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.HandheldScanner
        });

        var tagged = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "UV-TAG-DAMAGE01",
            Mode = DeviceOperatingMode.IssueRemote,
            DetectedAt = clock.Now.AddSeconds(12)
        });
        var snapshot = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = IssueRemoteCommands.Snapshot,
            Mode = DeviceOperatingMode.IssueRemote
        });

        Assert.Equal(ScanCommandAction.IssueTagged, tagged.Action);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 10, 0, 12, TimeSpan.FromHours(8)), repository.Records[0].Tags.Single().TaggedAt);
        Assert.Equal(ScanCommandAction.SnapshotCaptured, snapshot.Action);
        Assert.Equal(1, backend.SnapshotCount);
        Assert.Single(repository.Records[0].Snapshots);

        var undone = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = IssueTagDefaults.UndoBarcode,
            Mode = DeviceOperatingMode.IssueRemote
        });

        Assert.Equal(ScanCommandAction.IssueUndone, undone.Action);
        Assert.Empty(backend.OverlayTags);
    }

    [Theory]
    [InlineData(IssueTagDefaults.MissingBarcode, IssueTagDefaults.MissingTagId, "少件")]
    [InlineData(IssueTagDefaults.PurchaseBarcode, IssueTagDefaults.PurchaseTagId, "采购")]
    public async Task IssueRemoteCanApplyNewDefaultTags(
        string barcode,
        string expectedTagId,
        string expectedName)
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var profile = new ScannerProfile();
        var clock = new FakeClock(DateTimeOffset.Now);
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);
        await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.HandheldScanner
        });

        var acknowledgement = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = barcode,
            Mode = DeviceOperatingMode.IssueRemote
        });

        Assert.Equal(ScanCommandAction.IssueTagged, acknowledgement.Action);
        var tag = Assert.Single(repository.Records[0].Tags);
        Assert.Equal(expectedTagId, tag.TagId);
        Assert.Equal(expectedName, tag.TagName);
    }

    [Fact]
    public async Task IssueRemoteCanSaveNoteAndStopRecording()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var profile = new ScannerProfile();
        var clock = new FakeClock(DateTimeOffset.Now);
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);
        await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "SF1234567890",
            Mode = DeviceOperatingMode.HandheldScanner
        });

        var noted = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = $"{IssueRemoteCommands.NotePrefix}外箱破裂，商品待检查",
            Mode = DeviceOperatingMode.IssueRemote
        });
        var stopped = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = IssueRemoteCommands.Stop,
            Mode = DeviceOperatingMode.IssueRemote
        });

        Assert.Equal(ScanCommandAction.NoteUpdated, noted.Action);
        Assert.Equal("外箱破裂，商品待检查", repository.Records[0].Note);
        Assert.Equal(ScanCommandAction.RecordingStopped, stopped.Action);
        Assert.Equal(RecordingState.Completed, repository.Records[0].State);
        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public async Task IssueRemoteRejectsCommandsWhenStationIsIdle()
    {
        var repository = new InMemoryRepository();
        var backend = new FakeRecordingBackend(_temp);
        var profile = new ScannerProfile();
        var clock = new FakeClock(DateTimeOffset.Now);
        var coordinator = new RecordingCoordinator(repository, backend, new NullEventPublisher(), clock, profile);
        var router = new StationScanCommandRouter(coordinator, repository, clock, profile);

        var acknowledgement = await router.RouteAsync(new ScanCommand
        {
            DeviceId = "phone-a",
            Value = "UV-TAG-DAMAGE01",
            Mode = DeviceOperatingMode.IssueRemote
        });

        Assert.Equal(ScanCommandAction.Rejected, acknowledgement.Action);
        Assert.Contains("没有正在录像", acknowledgement.Message);
        Assert.Empty(repository.Records);
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
