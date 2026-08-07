using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    public const string Name = "WpfUi";
}

[Collection(WpfUiCollection.Name)]
public sealed class HistoryWindowUiTests
{
    [Fact]
    public void HistoryWindow_RendersReadOnlyRowsWithoutDispatcherException()
    {
        Exception? failure = null;
        var rendered = false;
        var selectionSynced = false;
        var stage = "thread-not-started";
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            global::UnpackVision.App.App? app = null;
            HistoryWindow? window = null;
            try
            {
                stage = "creating-application";
                app = new global::UnpackVision.App.App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                dispatcher = app.Dispatcher;
                stage = "initializing-application-resources";
                app.InitializeComponent();
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
                stage = "creating-history-window";
                window = new HistoryWindow(repository, new LocalSettings())
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.ToolWindow,
                    Left = -10_000,
                    Top = -10_000
                };

                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                app.DispatcherUnhandledException += (_, eventArgs) =>
                {
                    failure = eventArgs.Exception;
                    eventArgs.Handled = true;
                    frame.Continue = false;
                    Dispatcher.ExitAllFrames();
                };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) =>
                {
                    if (window.FindName("ResultCountText") is TextBlock result &&
                        result.Text.Contains("2 条结果", StringComparison.Ordinal))
                    {
                        rendered = true;
                        var grid = Assert.IsType<DataGrid>(window.FindName("HistoryGrid"));
                        var header = Assert.IsType<CheckBox>(window.FindName("SelectAllRowsCheckBox"));
                        grid.SelectedItems.Add(grid.Items[0]);
                        Assert.Null(header.IsChecked);
                        header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(2, grid.SelectedItems.Count);
                        Assert.True(header.IsChecked);
                        header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Empty(grid.SelectedItems);
                        Assert.False(header.IsChecked);
                        selectionSynced = true;
                        timer.Stop();
                        frame.Continue = false;
                        // Selection events can open nested WPF dispatcher frames. Exit
                        // all of them so a successful render cannot look like a hang.
                        Dispatcher.ExitAllFrames();
                    }
                    else if (DateTime.UtcNow >= deadline)
                    {
                        failure = new TimeoutException("历史记录窗口未在限定时间内完成首屏渲染。");
                        timer.Stop();
                        frame.Continue = false;
                        Dispatcher.ExitAllFrames();
                    }
                };

                stage = "showing-history-window";
                window.Show();
                timer.Start();
                stage = "pumping-dispatcher";
                Dispatcher.PushFrame(frame);
                stage = "dispatcher-finished";
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                stage = "closing-window";
                window?.Close();
                stage = "shutting-down-application";
                app?.Shutdown();
                stage = "thread-finished";
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref selectionSynced) || Volatile.Read(ref failure) is not null,
                TimeSpan.FromSeconds(15)),
            $"WPF 历史窗口未完成验收，阶段：{stage}，已渲染：{rendered}，选择同步：{selectionSynced}。");
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        // The WPF test host can retain an internal nested dispatcher frame after
        // selection automation. The background STA must not turn a completed UI
        // assertion into a false timeout; testhost teardown owns final disposal.
        _ = thread.Join(TimeSpan.FromSeconds(1));
        Assert.Null(failure);
        Assert.True(rendered);
        Assert.True(selectionSynced);
    }
}
