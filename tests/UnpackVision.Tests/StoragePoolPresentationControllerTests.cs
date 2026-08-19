using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Core.Recording;

namespace UnpackVision.Tests;

public sealed class StoragePoolPresentationControllerTests
{
    [Fact]
    public void LegacyTarget_RemainsFirstAndPrimaryRootTracksOrdering()
    {
        var options = StoragePoolOptions.FromLegacyRecordingRoot(@"C:\Recordings");
        var controller = CreateController(options);

        var added = controller.AddTarget(@"D:\Recordings");
        Assert.Equal(@"C:\Recordings", controller.GetPrimaryRecordingRoot());

        Assert.True(controller.MoveTarget(added, -1));
        var saved = controller.BuildOptions();

        Assert.Equal(@"D:\Recordings", controller.GetPrimaryRecordingRoot());
        Assert.Equal(new[] { 0, 1 }, saved.Targets.Select(target => target.Priority));
        Assert.Equal(StoragePoolOptions.LegacyTargetId, saved.Targets[1].Id);
    }

    [Fact]
    public void AddTarget_RejectsDuplicateRootWithoutChangingDraft()
    {
        var controller = CreateController(StoragePoolOptions.FromLegacyRecordingRoot(@"C:\Recordings"));

        var exception = Assert.Throws<ArgumentException>(() =>
            controller.AddTarget(@"C:\Recordings\."));

        Assert.Contains("已经在盘位列表", exception.Message);
        Assert.Single(controller.Targets);
    }

    [Fact]
    public void BuildOptions_RequiresOneEnabledTarget()
    {
        var controller = CreateController(StoragePoolOptions.FromLegacyRecordingRoot(@"C:\Recordings"));
        controller.Targets[0].Enabled = false;

        var exception = Assert.Throws<ArgumentException>(() => controller.BuildOptions());

        Assert.Contains("至少启用一个", exception.Message);
    }

    [Fact]
    public async Task Refresh_ProjectsCapacityWarningAndCapturesStableVolumeId()
    {
        var options = StoragePoolOptions.FromLegacyRecordingRoot(@"D:\Recordings");
        var monitor = new FakeMonitor(new StoragePoolState(
        [
            new StorageTargetState(
                StoragePoolOptions.LegacyTargetId,
                "主录像盘",
                @"D:\Recordings",
                "volume-d",
                0,
                StorageTargetHealth.Warning,
                100 * StoragePoolOptions.Gibibyte,
                12 * StoragePoolOptions.Gibibyte,
                0,
                12 * StoragePoolOptions.Gibibyte,
                10 * StoragePoolOptions.Gibibyte,
                12,
                TimeSpan.FromHours(1.5),
                StorageWarningReason.LowFreeSpacePercentage |
                StorageWarningReason.LowEstimatedRecordingTime,
                null)
        ]));
        var controller = new StoragePoolPresentationController(
            options,
            monitor,
            StoragePoolOptions.Gibibyte,
            StoragePoolOptions.Gibibyte);

        await controller.RefreshAsync();

        var row = Assert.Single(controller.Targets);
        Assert.Equal("volume-d", row.VolumeId);
        Assert.Equal("空间预警", row.StatusText);
        Assert.Contains("12%", row.CapacityText);
        Assert.Contains("1.5 小时", row.RemainingText);
        Assert.Contains("低于 15%", row.WarningText);
        Assert.Contains("不足 2 小时", row.WarningText);
        Assert.Contains("可用 1/1", controller.SummaryText);
    }

    [Fact]
    public void EstimatedRecordingRate_IncludesEveryEnabledCameraAndOptionalComposite()
    {
        var cameras = new[]
        {
            new CameraProfile { Enabled = true, Width = 1920, Height = 1080, FramesPerSecond = 15 },
            new CameraProfile { Enabled = true, Width = 1280, Height = 720, FramesPerSecond = 10 },
            new CameraProfile { Enabled = false, Width = 3840, Height = 2160, FramesPerSecond = 30 }
        };

        var withoutComposite = StoragePoolPresentationController.EstimateBytesPerHour(cameras, false);
        var withComposite = StoragePoolPresentationController.EstimateBytesPerHour(cameras, true);

        Assert.True(withoutComposite > 0);
        Assert.True(withComposite > withoutComposite);
    }

    private static StoragePoolPresentationController CreateController(StoragePoolOptions options) =>
        new(options, new FakeMonitor(new StoragePoolState([])), 0, 0);

    private sealed class FakeMonitor(StoragePoolState state) : IStoragePoolMonitor
    {
        public Task<StoragePoolState> InspectAsync(
            StoragePoolOptions options,
            StorageCapacityRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(state);
    }
}
