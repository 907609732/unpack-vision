using System.Windows;
using System.Windows.Controls;
using UnpackVision.App;
using UnpackVision.Core;

namespace UnpackVision.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class VideoPlayerWindowUiTests
{
    private readonly WpfTestHost _wpf;

    public VideoPlayerWindowUiTests(WpfTestHost wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void WindowRendersFourSynchronizedPlaceholdersAndKeepsExtraAssetSelectable()
    {
        VideoPlayerWindow? window = null;
        try
        {
            _wpf.Invoke(() =>
            {
                window = new VideoPlayerWindow(CreateRecord())
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.ToolWindow,
                    Left = -10_000,
                    Top = -10_000
                };
                window.Show();
            });

            _wpf.WaitUntil(
                () =>
                {
                    var playbackGrid = Assert.IsType<Grid>(window!.FindName("PlaybackGrid"));
                    var assetSelector = Assert.IsType<ItemsControl>(window.FindName("CameraAnglesControl"));
                    if (playbackGrid.Children.Count != 4 || assetSelector.Items.Count != 5)
                    {
                        return false;
                    }

                    var selectionSummary = Assert.IsType<TextBlock>(window.FindName("SelectionSummaryText"));
                    var status = Assert.IsType<TextBlock>(window.FindName("PlaybackStatusText"));
                    Assert.Contains("4/4", selectionSummary.Text, StringComparison.Ordinal);
                    Assert.Contains("4 路文件缺失", status.Text, StringComparison.Ordinal);
                    return true;
                },
                TimeSpan.FromSeconds(10),
                "同步回放窗口未在限定时间内完成四画面渲染。");
        }
        finally
        {
            if (window is not null)
            {
                _wpf.Invoke(window.Close);
            }
        }
    }

    private static ScanRecord CreateRecord()
    {
        var startedAt = DateTimeOffset.Now.AddMinutes(-1);
        var recordId = Guid.NewGuid();
        var assets = Enumerable.Range(1, 5)
            .Select(index => new RecordMediaAsset
            {
                RecordId = recordId,
                CameraId = $"camera-{index}",
                DisplayName = index == 1 ? "主机位" : $"机位 {index}",
                Role = index == 1 ? RecordMediaRole.Primary : RecordMediaRole.Angle,
                VideoPath = Path.Combine(Path.GetTempPath(), $"missing-sync-video-{Guid.NewGuid():N}.mp4"),
                CreatedAt = startedAt.AddMilliseconds(index),
                UpdatedAt = startedAt.AddMilliseconds(index)
            })
            .ToArray();
        return new ScanRecord
        {
            Id = recordId,
            TrackingNo = "SYNC-UI-001",
            State = RecordingState.Completed,
            ScannedAt = startedAt,
            RecordingStartedAt = startedAt,
            RecordingEndedAt = startedAt.AddSeconds(30),
            VideoPath = assets[0].VideoPath,
            CameraId = assets[0].CameraId,
            DefaultMediaAssetId = assets[0].Id,
            MediaAssets = assets,
            CreatedAt = startedAt,
            UpdatedAt = startedAt.AddSeconds(30)
        };
    }
}
