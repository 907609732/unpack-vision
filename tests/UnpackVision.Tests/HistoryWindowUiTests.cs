using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

[Collection(WpfUiCollection.Name)]
public sealed class HistoryWindowUiTests
{
    private readonly WpfTestHost _wpf;

    public HistoryWindowUiTests(WpfTestHost wpf)
    {
        _wpf = wpf;
    }

    [Fact]
    public void HistoryWindow_RendersReadOnlyRowsWithoutDispatcherException()
    {
        HistoryWindow? window = null;
        try
        {
            _wpf.Invoke(() =>
            {
                var repository = new InMemoryRepository();
                repository.Records.Add(new ScanRecord
                {
                    TrackingNo = "UI-RECORD-001",
                    State = RecordingState.Completed,
                    ScannedAt = DateTimeOffset.Now,
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                });
                repository.Records.Add(new ScanRecord
                {
                    TrackingNo = "UI-RECORD-002",
                    State = RecordingState.Completed,
                    ScannedAt = DateTimeOffset.Now.AddSeconds(-1),
                    CreatedAt = DateTimeOffset.Now.AddSeconds(-1),
                    UpdatedAt = DateTimeOffset.Now.AddSeconds(-1)
                });
                window = new HistoryWindow(repository, new LocalSettings())
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
                    var result = Assert.IsType<TextBlock>(window!.FindName("ResultCountText"));
                    var grid = Assert.IsType<DataGrid>(window.FindName("HistoryGrid"));
                    if (grid.Items.Count != 2 ||
                        !result.Text.Contains("2 条结果", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var header = Assert.IsType<CheckBox>(window.FindName("SelectAllRowsCheckBox"));
                    grid.SelectedItems.Add(grid.Items[0]);
                    Assert.Null(header.IsChecked);
                    header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(2, grid.SelectedItems.Count);
                    Assert.True(header.IsChecked);
                    header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(grid.SelectedItems);
                    Assert.False(header.IsChecked);
                    return true;
                },
                TimeSpan.FromSeconds(10),
                "历史记录窗口未在限定时间内完成首屏渲染。");
        }
        finally
        {
            if (window is not null)
            {
                _wpf.Invoke(window.Close);
            }
        }
    }
}
