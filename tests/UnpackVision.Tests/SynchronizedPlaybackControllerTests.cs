using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class SynchronizedPlaybackControllerTests
{
    [Fact]
    public void DefaultCapabilitySelectsAtMostFourAndKeepsDefaultAndPrimaryAssets()
    {
        var record = CreateRecord(6);
        record.DefaultMediaAssetId = record.MediaAssets[5].Id;

        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);

        Assert.Equal(4, controller.SelectedAssets.Count);
        Assert.Contains(controller.SelectedAssets, asset => asset.Id == record.DefaultMediaAssetId);
        Assert.Contains(controller.SelectedAssets, asset => asset.Asset.Role == RecordMediaRole.Primary);
    }

    [Fact]
    public void SelectingBeyondCapabilityIsRejectedWithoutChangingSelection()
    {
        var record = CreateRecord(6);
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);
        var unselected = Assert.Single(controller.Assets.Skip(4).Take(1));

        var accepted = controller.TrySetSelected(unselected.Id, selected: true, out var message);

        Assert.False(accepted);
        Assert.Contains("最多同时回放 4 路", message, StringComparison.Ordinal);
        Assert.Equal(4, controller.SelectedAssets.Count);
    }

    [Fact]
    public void StartOffsetMapsSharedTimelineToLocalMediaPosition()
    {
        var record = CreateRecord(1);
        record.MediaAssets[0].StartOffset = TimeSpan.FromSeconds(3);
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);
        var assetId = record.MediaAssets[0].Id;
        controller.SetMediaDuration(assetId, TimeSpan.FromSeconds(10));

        controller.Seek(TimeSpan.FromSeconds(5));
        var playing = controller.GetDirective(assetId, TimeSpan.Zero, mediaReady: true, appliedSynchronizationVersion: 0);
        controller.Seek(TimeSpan.FromSeconds(2));
        var waiting = controller.GetDirective(assetId, TimeSpan.Zero, mediaReady: true, appliedSynchronizationVersion: 0);

        Assert.Equal(TimeSpan.FromSeconds(2), playing.DesiredLocalPosition);
        Assert.True(playing.ShouldRender);
        Assert.Equal(PlaybackPlaceholderKind.WaitingForStart, waiting.Placeholder);
        Assert.False(waiting.ShouldRender);
    }

    [Fact]
    public void SharedSpeedAdvancesTimelineAndStopsAtRecordEnd()
    {
        var record = CreateRecord(1, durationSeconds: 10);
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);
        controller.Seek(TimeSpan.FromSeconds(7));
        controller.SetSpeed(2);

        controller.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(10), controller.Position);
        Assert.False(controller.IsPlaying);
    }

    [Fact]
    public void OnlySelectedPrimaryAssetProvidesAudio()
    {
        var record = CreateRecord(3);
        record.DefaultMediaAssetId = record.MediaAssets[2].Id;
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);

        Assert.Equal(record.MediaAssets[0].Id, controller.AudioAssetId);
        Assert.NotEqual(record.DefaultMediaAssetId, controller.AudioAssetId);
    }

    [Fact]
    public void MissingAssetRemainsVisibleAsAPlaceholderInsteadOfDisappearing()
    {
        var record = CreateRecord(1);
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => false);
        var asset = Assert.Single(controller.SelectedAssets);

        var directive = controller.GetDirective(asset.Id, TimeSpan.Zero, mediaReady: false, appliedSynchronizationVersion: 0);

        Assert.False(asset.IsFileAvailable);
        Assert.Equal(PlaybackPlaceholderKind.MissingFile, directive.Placeholder);
        Assert.Contains("不存在", directive.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityGateCanLaterEnableSixteenStreamsWithoutChangingAlignmentRules()
    {
        var record = CreateRecord(16);
        var capabilities = new SynchronizedPlaybackCapabilities(16);

        var controller = new SynchronizedPlaybackController(record, capabilities, _ => true);

        Assert.Equal(16, controller.SelectedAssets.Count);
        Assert.Equal(16, controller.Capabilities.MaximumSimultaneousStreams);
    }

    [Fact]
    public void LastSelectedAssetCannotBeRemoved()
    {
        var record = CreateRecord(1);
        var controller = new SynchronizedPlaybackController(record, fileExists: _ => true);

        var accepted = controller.TrySetSelected(record.MediaAssets[0].Id, selected: false, out var message);

        Assert.False(accepted);
        Assert.Equal("至少保留一个回放画面。", message);
        Assert.Single(controller.SelectedAssets);
    }

    private static ScanRecord CreateRecord(int assetCount, int durationSeconds = 30)
    {
        var startedAt = new DateTimeOffset(2026, 8, 9, 10, 30, 0, TimeSpan.FromHours(8));
        var recordId = Guid.NewGuid();
        var assets = Enumerable.Range(0, assetCount)
            .Select(index => new RecordMediaAsset
            {
                Id = Guid.NewGuid(),
                RecordId = recordId,
                CameraId = $"camera-{index + 1}",
                DisplayName = index == 0 ? "主机位" : $"机位 {index + 1}",
                Role = index == 0 ? RecordMediaRole.Primary : index == assetCount - 1 ? RecordMediaRole.Composite : RecordMediaRole.Angle,
                VideoPath = $"C:\\recordings\\camera-{index + 1}.mp4",
                CreatedAt = startedAt.AddMilliseconds(index),
                UpdatedAt = startedAt.AddMilliseconds(index)
            })
            .ToArray();

        return new ScanRecord
        {
            Id = recordId,
            TrackingNo = "SYNC-PLAYBACK-001",
            State = RecordingState.Completed,
            ScannedAt = startedAt,
            RecordingStartedAt = startedAt,
            RecordingEndedAt = startedAt.AddSeconds(durationSeconds),
            VideoPath = assets[0].VideoPath,
            CameraId = assets[0].CameraId,
            DefaultMediaAssetId = assets[0].Id,
            MediaAssets = assets,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(durationSeconds)
        };
    }
}
