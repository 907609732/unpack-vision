using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using UnpackVision.Application.Recording;
using UnpackVision.Application.Scanning;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

internal sealed class PrimaryRecordingEmergencyStopGate
{
    private int _running;

    public bool TryBegin(RecordingState state) =>
        state == RecordingState.Recording && Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void Complete() => Volatile.Write(ref _running, 0);
}

public partial class MainWindow : Window, ICameraConfigurationPreviewHost
{
    internal bool IsRecordingOperationActive =>
        _coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving;

    internal async Task PrepareForUninstallAsync()
    {
        if (IsRecordingOperationActive)
        {
            throw new InvalidOperationException("正在录像、启动或保存时不能卸载");
        }
        await FlushIssueNoteAsync();
        await StationHostConnection.StopAsync(_lifetime.Token);
    }
    private static readonly Brush CameraReadyBackground = CreateFrozenBrush(232, 248, 239);
    private static readonly Brush CameraReadyForeground = CreateFrozenBrush(32, 126, 78);
    private readonly DispatcherTimer _recordingTimer;
    private readonly DispatcherTimer _imageControlTimer;
    private readonly DispatcherTimer _noteSaveTimer;
    private readonly DispatcherTimer _stationStateTimer;
    private readonly LoudSpeechService _speech = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _recentRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _cameraConfigurationPreviewGate = new(1, 1);
    private readonly PrimaryRecordingEmergencyStopGate _primaryRecordingEmergencyStopGate = new();
    private readonly ScannerInputSourceGate _scannerInputSourceGate = new();
    private readonly LocalSettingsStore _settingsStore = new();
    private LocalSettings _settings = new();
    private StorageOptions? _storageOptions;
    private IScanRecordRepository? _repository;
    private MultiCameraRecordingBackend? _recordingBackend;
    private bool _cameraConfigurationPreviewActive;
    private readonly Dictionary<string, Image> _cameraPreviewImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _cameraPreviewTiles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CameraPreviewSlotPlan> _cameraPreviewSlotPlans = [];
    private string? _expandedCameraId;
    private RecordingCoordinator? _coordinator;
    private IScanCommandLedger? _scanCommandLedger;
    private StationScanCommandRouter? _stationRouter;
    private DesktopCommandListener? _desktopCommandListener;
    private SyncDispatcher? _syncDispatcher;
    private RawInputScannerCapture? _rawScanner;
    private string? _lastProcessedCode;
    private DateTimeOffset _lastProcessedAt;
    private DateTimeOffset? _recordingStartedAt;
    private DateTimeOffset? _nextTimeoutAt;
    private bool _fullScreen;
    private bool _shutdownStarted;
    private bool _allowClose;
    private bool _updatingCameraSourceSelector;
    private readonly ConcurrentDictionary<string, byte> _previewUpdatesPending = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingIssueNote;
    private bool _designerPageVisible;
    private bool _stationStatePollActive;
    private Guid? _mirroredStationRecordId;
    private string _displayedTrackingNo = string.Empty;
    private string _lastCameraStatusText = string.Empty;
    private string _lastCameraRuntimeKey = string.Empty;
    private readonly List<string> _lastSnapshotPaths = [];

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeWindowAppearance();
        RecentItemsControl.ItemsSource = Array.Empty<RecentRecordingItem>();
        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnRecordingTimer, Dispatcher);
        _imageControlTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, OnImageControlTimer, Dispatcher);
        _noteSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, SaveNoteTimer_OnTick, Dispatcher);
        _stationStateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, OnStationStateTimer, Dispatcher);
        Loaded += OnLoaded;
        Closing += OnClosing;
        App.Updates.StatusChanged += Updates_OnStatusChanged;
        App.Updates.UpdateReady += Updates_OnUpdateReady;
        RenderUpdateBanner(App.Updates.Status);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            PopulateCameraSourceSelector();
            ApplySettingsVisuals();
            _storageOptions = new StorageOptions
            {
                RecordingRoot = _settings.RecordingRoot,
                StoragePool = _settings.StoragePool,
                MaximumRecordingMinutes = _settings.MaximumRecordingMinutes
            };
            var excelOptions = CreateExcelOptions();

            var sqliteRepository = new SqliteScanRecordRepository(_storageOptions);
            _repository = new PortableCatalogScanRecordRepository(
                sqliteRepository,
                _storageOptions);
            await _repository.InitializeAsync(_lifetime.Token);
            var interrupted = await new InterruptedRecordingRecovery(_repository, new SystemClock())
                .MarkInterruptedAsync(_lifetime.Token);

            _recordingBackend = CreateRecordingBackend(_settings.CameraRig);
            BuildCameraPreviewGrid();
            _coordinator = new RecordingCoordinator(
                _repository,
                _recordingBackend,
                new NullEventPublisher(),
                new SystemClock(),
                _settings.Scanner);
            _coordinator.StateChanged += Coordinator_OnStateChanged;
            _scanCommandLedger = new SqliteScanCommandLedger(_storageOptions, new SystemClock());
            await _scanCommandLedger.InitializeAsync(_lifetime.Token);
            RebuildStationRouter();
            await StationHostConnection.EnsureRunningAsync(_lifetime.Token);
            _desktopCommandListener = new DesktopCommandListener(RouteMobileCommandAsync, GetDesktopStationState);
            await _desktopCommandListener.StartAsync(_lifetime.Token);
            QuickIssueTagsControl.ItemsSource = _settings.IssueTags.Where(item => item.Enabled).OrderBy(item => item.SortOrder).ToArray();
            LabelDesigner.ConfigureIssueTags(_settings.IssueTags);

            _rawScanner = new RawInputScannerCapture(this, () => _settings.Scanner);
            _rawScanner.BarcodeScanned += RawScanner_OnBarcodeScanned;
            _syncDispatcher = new SyncDispatcher(_repository, [new ExcelConnector(excelOptions)], new SystemClock());
            _ = RunSyncLoopAsync(_lifetime.Token);

            _stationStateTimer.Start();
            await PollStationStateAsync();

            await RefreshRecentAsync();
            FooterText.Text = interrupted == 0
                ? "本地数据与 Excel 同步队列已就绪"
                : $"已发现 {interrupted} 条上次中断录像，临时文件已保留";

            if (_settings.ShowLivePreview)
            {
                try
                {
                    await _recordingBackend.StartPreviewAsync(_lifetime.Token);
                    UpdateCameraRuntimeInfo();
                }
                catch (Exception cameraException)
                {
                    ShowCameraError(cameraException.Message);
                }
            }
            else
            {
                CameraStatusText.Text = "实时预览已关闭";
            }
            ScannerInput.Focus();
            if (_settings.AutoCheckUpdates)
            {
                _ = App.Updates.CheckAndDownloadAsync(force: false, _lifetime.Token);
            }
            ScheduleLegacyMigrationRepairOffer();
        }
        catch (Exception ex)
        {
            FooterText.Text = "初始化失败";
            MessageBox.Show(this, ex.ToString(), "初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ScannerInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _coordinator is null)
        {
            return;
        }
        e.Handled = true;
        var value = ScannerInput.Text;
        ScannerInput.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        var fallbackTicket = _scannerInputSourceGate.BeginFallback(Environment.TickCount64);
        try
        {
            await Task.Delay(ScannerInputSourceGate.FallbackGraceMilliseconds, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (!_scannerInputSourceGate.ShouldProcessFallback(fallbackTicket, Environment.TickCount64))
        {
            return;
        }
        // If Raw Input missed this scan's terminator but the legacy keyboard path received it,
        // the Raw buffer still contains the completed parcel. Drop it before the next scan can
        // append another tracking number and produce A+B.
        _rawScanner?.DiscardBufferedInput();
        await ProcessBarcodeAsync(value, "焦点输入框");
    }

    private async void RawScanner_OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        // A keyboard-mode scanner also produces legacy TextBox keystrokes. Raw Input is the
        // authoritative device-aware path, so remove the mirrored legacy text before its Enter
        // event can replay the same scan or carry old characters into the next parcel.
        _scannerInputSourceGate.ObserveRaw(Environment.TickCount64);
        ScannerInput.Clear();
        await ProcessBarcodeAsync(e.Value, e.DeviceName);
    }

    private async Task ProcessBarcodeAsync(string value, string deviceName)
    {
        if (_coordinator is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (_cameraConfigurationPreviewActive)
        {
            FooterText.Text = "正在配置并预览摄像头，请关闭预览后再扫码";
            Speak("正在配置摄像头，请稍后再扫码");
            return;
        }
        var normalized = _settings.Scanner.Normalize(value);
        var now = DateTimeOffset.Now;
        if (string.Equals(_lastProcessedCode, normalized, StringComparison.Ordinal) &&
            now - _lastProcessedAt < TimeSpan.FromMilliseconds(_settings.Scanner.DebounceMilliseconds))
        {
            FooterText.Text = $"已忽略重复扫码：{normalized}";
            return;
        }
        _lastProcessedCode = normalized;
        _lastProcessedAt = now;
        FooterText.Text = $"扫码设备：{deviceName}";

        var issueMatch = IssueTagBarcodeRouter.Match(normalized, _settings.IssueTags);
        if (issueMatch.Action != IssueBarcodeAction.None)
        {
            await ProcessIssueBarcodeAsync(issueMatch);
            return;
        }

        await FlushIssueNoteAsync();
        await _coordinator.ProcessScanAsync(normalized, _settings.Workflow, _lifetime.Token);
        await RefreshRecentAsync();
    }

    private void Coordinator_OnStateChanged(object? sender, ScanResult result) =>
        Dispatcher.BeginInvoke(() => ApplyResult(result));

    private void ApplyResult(ScanResult result)
    {
        FooterText.Text = result.Message;
        switch (result.Action)
        {
            case ScanAction.Started:
                _lastSnapshotPaths.Clear();
                ShowRecordingUi(result.Record!);
                Speak(result.Message.Contains("上一单", StringComparison.Ordinal)
                    ? "上一单已保存，开始录制下一单"
                    : result.Record?.DuplicateOf is null ? "开始录制" : "开始录制，重复单号");
                break;
            case ScanAction.Stopped:
                if (result.Message.Contains("已保存", StringComparison.Ordinal))
                {
                    ShowIdleUi("录像已保存，可以继续扫描下一个快递");
                    Speak("录像已保存");
                    if (App.Updates.Status.ReadyToInstall)
                    {
                        Updates_OnUpdateReady(App.Updates, EventArgs.Empty);
                    }
                }
                else
                {
                    CurrentStateText.Text = "正在保存…";
                    StateDot.Fill = Brushes.Orange;
                }
                break;
            case ScanAction.Busy:
                Speak("正在处理录像，请稍后再扫");
                break;
            case ScanAction.StopIgnored:
                Speak("当前没有正在录制的视频");
                break;
            case ScanAction.Invalid:
                CurrentStateText.Text = "单号无效";
                StateDot.Fill = Brushes.Orange;
                Speak(result.Message);
                break;
            case ScanAction.Failed:
                ShowIdleUi(result.Message);
                CurrentStateText.Text = "录像异常";
                StateDot.Fill = Brushes.Red;
                Speak(result.Message);
                break;
        }
        ScannerInput.Focus();
    }

    private void Updates_OnUpdateReady(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RenderUpdateBanner(App.Updates.Status);
            if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
            {
                FooterText.Text = "新版本已下载，将在本单录像保存后保持提醒";
            }
        });

    private void Updates_OnStatusChanged(object? sender, DesktopUpdateStatus status) =>
        Dispatcher.BeginInvoke(() => RenderUpdateBanner(status));

    private void RenderUpdateBanner(DesktopUpdateStatus status)
    {
        if (!status.ReadyToInstall && string.IsNullOrWhiteSpace(status.AvailableVersion))
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerTitle.Text = status.IsCritical
            ? $"安全更新 {status.AvailableVersion}"
            : $"发现版本 {status.AvailableVersion}";
        UpdateBannerMessage.Text = status.Message;
        UpdateBanner.Background = status.IsCritical
            ? new SolidColorBrush(Color.FromRgb(255, 244, 232))
            : new SolidColorBrush(Color.FromRgb(234, 245, 255));
        UpdateInstallButton.IsEnabled = status.ReadyToInstall;
        UpdateInstallButton.Content = status.ReadyToInstall ? "重启并安装" : "正在下载";
    }

    private async void UpdateInstallButton_OnClick(object sender, RoutedEventArgs e) =>
        await TryApplyUpdateAsync();

    private void UpdateNotesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var url = App.Updates.Status.ReleaseNotesUrl ?? ProductInfo.LatestReleaseUrl;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void UpdateDismissButton_OnClick(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    internal async Task TryApplyUpdateAsync()
    {
        if (!App.Updates.Status.ReadyToInstall)
        {
            MessageBox.Show(this, "当前没有已下载的更新。", "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
        {
            MessageBox.Show(this, "正在录像或保存，完成当前包裹后才能更新。", "暂不能更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await FlushIssueNoteAsync();
            FooterText.Text = "正在关闭后台组件并安装更新…";
            await StationHostConnection.StopAsync(_lifetime.Token);
            App.Updates.ApplyAndRestart();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"安装更新失败：{exception.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowRecordingUi(ScanRecord record)
    {
        _displayedTrackingNo = record.TrackingNo;
        CurrentStateText.Text = record.DuplicateOf is null ? "正在录像" : "正在录像 · 重复单号";
        StateDot.Fill = new SolidColorBrush(Color.FromRgb(229, 71, 71));
        RecordingBadge.Visibility = Visibility.Visible;
        WatermarkPreview.Visibility = Visibility.Visible;
        IdleActionPanel.Visibility = Visibility.Collapsed;
        RecordingActionPanel.Visibility = Visibility.Visible;
        AnimateIn(RecordingActionPanel);
        _recordingStartedAt = record.RecordingStartedAt ?? DateTimeOffset.Now;
        _nextTimeoutAt = _recordingStartedAt.Value.AddMinutes(_settings.MaximumRecordingMinutes);
        _recordingTimer.Start();
        UpdateCurrentTrackingBarcode(record.TrackingNo);
        UpdateIssueUi(record);
    }

    private void ShowIdleUi(string message)
    {
        _displayedTrackingNo = string.Empty;
        CurrentStateText.Text = "等待扫码";
        StateDot.Fill = new SolidColorBrush(Color.FromRgb(67, 181, 129));
        RecordingBadge.Visibility = Visibility.Collapsed;
        WatermarkPreview.Visibility = Visibility.Collapsed;
        RecordingActionPanel.Visibility = Visibility.Collapsed;
        IdleActionPanel.Visibility = Visibility.Visible;
        CurrentTrackingBarcodeImage.Source = null;
        _noteSaveTimer.Stop();
        _loadingIssueNote = true;
        IssueNoteInput.Clear();
        _loadingIssueNote = false;
        ActiveIssueSummaryText.Text = "当前没有异常标签";
        WatermarkIssueText.Text = string.Empty;
        AnimateIn(IdleActionPanel);
        _recordingTimer.Stop();
        _recordingStartedAt = null;
        _nextTimeoutAt = null;
        FooterText.Text = message;
    }

    private async void OnRecordingTimer(object? sender, EventArgs e)
    {
        if (_recordingStartedAt is null)
        {
            return;
        }
        var now = DateTimeOffset.Now;
        var elapsed = now - _recordingStartedAt.Value;
        RecordingTimeText.Text = $"录制中 {elapsed:hh\\:mm\\:ss}";
        WatermarkTimeText.Text = now.ToString("yyyy-MM-dd HH:mm:ss");
        WatermarkTrackingText.Text = $"快递单号：{_displayedTrackingNo}";
        if (_nextTimeoutAt is not null && now >= _nextTimeoutAt)
        {
            _nextTimeoutAt = now.AddMinutes(_settings.MaximumRecordingMinutes);
            Speak("录像超时，请确认是否继续录制");
            var answer = MessageBox.Show(
                this,
                $"当前录像已经超过 {_settings.MaximumRecordingMinutes} 分钟。\n\n选择“是”继续录像，选择“否”停止并保存。",
                "录像超时提醒",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);
            if (answer == MessageBoxResult.No && _coordinator is not null)
            {
                await _coordinator.EmergencyStopAsync(_lifetime.Token);
                await RefreshRecentAsync();
            }
        }
    }

    private void RecordingBackend_OnPreviewFrameReady(object? sender, MultiCameraPreviewFrameEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || !_previewUpdatesPending.TryAdd(e.CameraId, 0))
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                if (_cameraPreviewImages.TryGetValue(e.CameraId, out var image))
                {
                    image.Source = UiImage.FromBytes(e.JpegBytes);
                }
                CameraPlaceholder.Visibility = Visibility.Collapsed;
                var statusText = _recordingBackend?.IsRecording == true ? "相机正常 · 正在录像" : "相机正常 · 实时预览";
                if (!string.Equals(_lastCameraStatusText, statusText, StringComparison.Ordinal))
                {
                    CameraStatusBadge.Background = CameraReadyBackground;
                    CameraStatusText.Foreground = CameraReadyForeground;
                    CameraStatusText.Text = statusText;
                    _lastCameraStatusText = statusText;
                }
                UpdateCameraRuntimeInfo();
            }
            finally
            {
                _previewUpdatesPending.TryRemove(e.CameraId, out _);
            }
        });
    }

    private void RecordingBackend_OnCameraStateChanged(object? sender, CameraRuntimeState state) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_cameraPreviewTiles.TryGetValue(state.CameraId, out var tile))
            {
                tile.BorderBrush = state.ConnectionState == CameraConnectionState.Connected
                    ? new SolidColorBrush(Color.FromRgb(72, 190, 116))
                    : new SolidColorBrush(Color.FromRgb(235, 92, 92));
            }
            if (state.IsPrimary && state.ConnectionState is CameraConnectionState.Failed or CameraConnectionState.Missing)
            {
                ShowCameraError(state.Message ?? "主机位不可用");
            }
            UpdateCameraRuntimeInfo();
        });

    private void RecordingBackend_OnStorageWarningRaised(object? sender, RecordingStorageWarningEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            var message = string.IsNullOrWhiteSpace(e.Allocation.WarningMessage)
                ? $"录像盘位“{e.Allocation.TargetDisplayName}”空间即将不足，请尽快增加或更换硬盘。"
                : e.Allocation.WarningMessage;
            FooterText.Text = message;
            Speak("录像硬盘空间不足，请尽快处理");
        });

    private void RecordingBackend_OnPrimaryRecordingFailed(object? sender, PrimaryRecordingFailureEventArgs e) =>
        Dispatcher.BeginInvoke(async () => await HandlePrimaryRecordingFailureAsync(e));

    private async Task HandlePrimaryRecordingFailureAsync(PrimaryRecordingFailureEventArgs failure)
    {
        if (_coordinator is null ||
            _coordinator.CurrentRecord?.Id != failure.RecordId ||
            !_primaryRecordingEmergencyStopGate.TryBegin(_coordinator.State))
        {
            return;
        }

        try
        {
            CurrentStateText.Text = "主机位故障，正在安全停止";
            StateDot.Fill = Brushes.Red;
            FooterText.Text = $"{failure.Message}，正在保留可恢复视频并结束当前单号。";
            Speak("主机位录像故障，正在安全停止");
            await FlushIssueNoteAsync();
            await _coordinator.EmergencyStopAsync(_lifetime.Token);
            await RefreshRecentAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CurrentStateText.Text = "自动停止失败";
            StateDot.Fill = Brushes.Red;
            FooterText.Text = $"主机位故障后自动停止失败：{exception.Message}";
            Speak("自动停止录像失败，请立即检查");
        }
        finally
        {
            _primaryRecordingEmergencyStopGate.Complete();
        }
    }

    private void BuildCameraPreviewGrid()
    {
        CameraPreviewGrid.Children.Clear();
        CameraPreviewGrid.RowDefinitions.Clear();
        CameraPreviewGrid.ColumnDefinitions.Clear();
        _cameraPreviewImages.Clear();
        _cameraPreviewTiles.Clear();
        var cameras = _recordingBackend?.Rig.EnabledCameras ?? _settings.CameraRig.EnabledCameras;
        var viewCount = CameraPreviewLayoutPlanner.ResolveViewCount(
            _settings.CameraRig.Mode,
            _settings.PreviewLayout.ViewCount,
            CameraRigOptions.MaximumEnabledCameras);
        _cameraPreviewSlotPlans = CameraPreviewLayoutPlanner.Build(
            viewCount,
            cameras,
            _settings.PreviewLayout.CameraIds);
        if (_settings.CameraRig.Mode == CameraRigMode.MultiCamera)
        {
            _settings.PreviewLayout.ViewCount = viewCount;
            _settings.PreviewLayout.CameraIds = _cameraPreviewSlotPlans
                .Select(plan => plan.Camera?.Id ?? string.Empty)
                .ToList();
        }
        // Layout geometry belongs to the view planner. Deriving it from the plans keeps
        // the monitoring wall generic when the certified product limit grows again.
        var columns = Math.Max(1, _cameraPreviewSlotPlans.Max(plan => plan.Column + plan.ColumnSpan));
        var rows = Math.Max(1, _cameraPreviewSlotPlans.Max(plan => plan.Row + plan.RowSpan));
        for (var index = 0; index < columns; index++) CameraPreviewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var index = 0; index < rows; index++) CameraPreviewGrid.RowDefinitions.Add(new RowDefinition());
        foreach (var plan in _cameraPreviewSlotPlans)
        {
            var profile = plan.Camera;
            var image = new Image
            {
                Stretch = _settings.FaceZoomEnabled ? Stretch.UniformToFill : Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible
            };
            var label = new TextBlock
            {
                Text = profile is null
                    ? $"画面 {plan.SlotIndex + 1} · 右键选择摄像头"
                    : profile.IsPrimary
                        ? $"● {profile.DisplayName} · 主机位"
                        : $"● {profile.DisplayName}",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(185, 26, 28, 32)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = label
            };
            var content = new Grid();
            content.Children.Add(image);
            if (profile is null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "右键此画面选择摄像头",
                    Foreground = new SolidColorBrush(Color.FromRgb(134, 140, 151)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            content.Children.Add(badge);
            var tile = new Border
            {
                Tag = plan.SlotIndex,
                Background = new SolidColorBrush(Color.FromRgb(18, 20, 24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 68, 74)),
                BorderThickness = new Thickness(1),
                Margin = viewCount == 1 ? new Thickness(0) : new Thickness(3),
                CornerRadius = new CornerRadius(viewCount == 1 ? 0 : 14),
                ClipToBounds = true,
                Child = content,
                ContextMenu = CreatePreviewCameraMenu(plan.SlotIndex, profile?.Id)
            };
            if (profile is not null)
            {
                tile.MouseLeftButtonUp += (_, _) => ToggleExpandedCamera(profile.Id);
            }
            Grid.SetColumn(tile, plan.Column);
            Grid.SetRow(tile, plan.Row);
            Grid.SetColumnSpan(tile, plan.ColumnSpan);
            Grid.SetRowSpan(tile, plan.RowSpan);
            CameraPreviewGrid.Children.Add(tile);
            if (profile is not null)
            {
                _cameraPreviewImages[profile.Id] = image;
                _cameraPreviewTiles[profile.Id] = tile;
            }
        }
        UpdatePreviewLayoutButtons();
    }

    private ContextMenu CreatePreviewCameraMenu(int slotIndex, string? selectedCameraId)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => PopulatePreviewCameraMenu(menu, slotIndex, selectedCameraId);
        PopulatePreviewCameraMenu(menu, slotIndex, selectedCameraId);
        return menu;
    }

    private void PopulatePreviewCameraMenu(ContextMenu menu, int slotIndex, string? selectedCameraId)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem
        {
            Header = $"画面 {slotIndex + 1} 显示",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold
        });
        menu.Items.Add(new Separator());
        var choices = PreviewCameraChoiceBuilder.Build(
            _settings.CameraRig,
            WindowsCameraDiscovery.Enumerate());
        AddPreviewCameraMenuGroup(
            menu,
            "当前机位",
            choices.Where(choice => choice.Group == PreviewCameraChoiceGroup.Active),
            slotIndex,
            selectedCameraId);
        AddPreviewCameraMenuGroup(
            menu,
            "其他已配置来源",
            choices.Where(choice => choice.Group == PreviewCameraChoiceGroup.Configured),
            slotIndex,
            selectedCameraId);
        AddPreviewCameraMenuGroup(
            menu,
            "Windows 本地摄像头（USB / iVCam）",
            choices.Where(choice => choice.Group == PreviewCameraChoiceGroup.LocalDevice),
            slotIndex,
            selectedCameraId);
        menu.Items.Add(new Separator());
        var refreshItem = new MenuItem { Header = "刷新摄像头列表" };
        refreshItem.Click += (_, _) => PopulatePreviewCameraMenu(menu, slotIndex, selectedCameraId);
        menu.Items.Add(refreshItem);
        var settingsItem = new MenuItem { Header = "管理机位…" };
        settingsItem.Click += OpenCameraSettingsFromPreview_OnClick;
        menu.Items.Add(settingsItem);
    }

    private void AddPreviewCameraMenuGroup(
        ContextMenu menu,
        string groupTitle,
        IEnumerable<PreviewCameraChoice> choices,
        int slotIndex,
        string? selectedCameraId)
    {
        var items = choices.ToArray();
        if (items.Length == 0)
        {
            return;
        }
        if (menu.Items.Count > 2)
        {
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem
        {
            Header = groupTitle,
            IsEnabled = false,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 116, 126))
        });
        foreach (var choice in items)
        {
            var assignable = choice.Action == PreviewCameraChoiceAction.AssignPreview;
            var item = new MenuItem
            {
                Header = assignable
                    ? choice.Label
                    : choice.Group == PreviewCameraChoiceGroup.Configured
                        ? $"{choice.Label}  ·  未启用，添加到机位方案…"
                        : $"{choice.Label}  ·  添加到机位方案…",
                IsCheckable = assignable,
                IsChecked = assignable && !string.IsNullOrWhiteSpace(choice.CameraId) &&
                    string.Equals(choice.CameraId, selectedCameraId, StringComparison.OrdinalIgnoreCase),
                Tag = new PreviewCameraAssignment(slotIndex, choice)
            };
            item.Click += AssignPreviewCamera_OnClick;
            menu.Items.Add(item);
        }
    }

    private async void PreviewLayoutButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings.CameraRig.Mode != CameraRigMode.MultiCamera)
        {
            return;
        }
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var viewCount))
        {
            return;
        }
        _settings.PreviewLayout.ViewCount = Math.Clamp(viewCount, 1, CameraRigOptions.MaximumEnabledCameras);
        _expandedCameraId = null;
        BuildCameraPreviewGrid();
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        FooterText.Text = $"已切换为 {_settings.PreviewLayout.ViewCount} 画面；右键画面可以选择摄像头";
    }

    private async void AssignPreviewCamera_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: PreviewCameraAssignment assignment })
        {
            return;
        }
        if (assignment.Choice.Action == PreviewCameraChoiceAction.ConfigureRig)
        {
            if (IsRecordingOperationActive)
            {
                FooterText.Text = "录像过程中只能调整已启用机位的预览位置；请结束录像后再添加机位";
                return;
            }

            FooterText.Text = $"请在机位设置中添加或启用 {assignment.Choice.Label}";
            await OpenSettingsAsync(true);
            return;
        }

        var camera = _settings.CameraRig.EnabledCameras.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, assignment.Choice.CameraId, StringComparison.OrdinalIgnoreCase));
        if (camera is null)
        {
            FooterText.Text = "该摄像头尚未加入当前机位方案，请先进入设置添加或启用";
            return;
        }

        var previousLayout = new CameraPreviewLayoutSettings
        {
            ViewCount = _settings.PreviewLayout.ViewCount,
            CameraIds = [.. _settings.PreviewLayout.CameraIds]
        };
        _settings.PreviewLayout.CameraIds = CameraPreviewLayoutPlanner.Assign(
            _settings.PreviewLayout.CameraIds,
            _settings.PreviewLayout.ViewCount,
            assignment.SlotIndex,
            camera.Id).ToList();
        try
        {
            _expandedCameraId = null;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            _recordingBackend?.SelectCamera(camera.Id);
            BuildCameraPreviewGrid();
            SelectCurrentCameraSourceChoice();
            UpdateCameraRuntimeInfo();
            FooterText.Text = $"画面 {assignment.SlotIndex + 1} 已切换为 {camera.DisplayName}";
        }
        catch (Exception ex)
        {
            _settings.PreviewLayout = previousLayout;
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
            ShowCameraError($"切换摄像头失败：{ex.Message}");
        }
    }

    private async void OpenCameraSettingsFromPreview_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsRecordingOperationActive)
        {
            FooterText.Text = "录像过程中只能调整预览位置；请结束录像后再管理机位方案";
            return;
        }
        await OpenSettingsAsync(true);
    }

    private void UpdatePreviewLayoutButtons()
    {
        PreviewLayoutOptionsPanel.Visibility = _settings.CameraRig.Mode == CameraRigMode.MultiCamera
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var (button, count) in new[]
                 {
                     (PreviewLayout1Button, 1),
                     (PreviewLayout2Button, 2),
                     (PreviewLayout3Button, 3),
                     (PreviewLayout4Button, 4),
                     (PreviewLayout8Button, 8),
                     (PreviewLayout16Button, 16)
                 })
        {
            var selected = count == _settings.PreviewLayout.ViewCount;
            button.Background = selected
                ? new SolidColorBrush(Color.FromRgb(22, 119, 255))
                : Brushes.Transparent;
            button.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(74, 79, 87));
            button.BorderThickness = new Thickness(0);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void ToggleExpandedCamera(string cameraId)
    {
        if (!_cameraPreviewTiles.ContainsKey(cameraId))
        {
            return;
        }
        _expandedCameraId = string.Equals(_expandedCameraId, cameraId, StringComparison.OrdinalIgnoreCase)
            ? null
            : cameraId;
        foreach (var (id, tile) in _cameraPreviewTiles)
        {
            tile.Visibility = _expandedCameraId is null || string.Equals(id, _expandedCameraId, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (_expandedCameraId is not null && string.Equals(id, _expandedCameraId, StringComparison.OrdinalIgnoreCase))
            {
                Grid.SetRow(tile, 0);
                Grid.SetColumn(tile, 0);
                Grid.SetRowSpan(tile, Math.Max(1, CameraPreviewGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(tile, Math.Max(1, CameraPreviewGrid.ColumnDefinitions.Count));
            }
            else
            {
                var plan = _cameraPreviewSlotPlans.FirstOrDefault(item =>
                    string.Equals(item.Camera?.Id, id, StringComparison.OrdinalIgnoreCase));
                if (plan is not null)
                {
                    Grid.SetRow(tile, plan.Row);
                    Grid.SetColumn(tile, plan.Column);
                    Grid.SetRowSpan(tile, plan.RowSpan);
                    Grid.SetColumnSpan(tile, plan.ColumnSpan);
                }
            }
        }
        _recordingBackend?.SelectCamera(cameraId);
        SelectCurrentCameraSourceChoice();
        UpdateCameraRuntimeInfo();
    }

    private void CameraPreviewGrid_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Individual tiles handle the interaction; this named handler keeps the
        // preview surface keyboard/mouse contract stable for XAML tests.
    }

    private sealed record PreviewCameraAssignment(int SlotIndex, PreviewCameraChoice Choice);

    private void ShowCameraError(string message)
    {
        _lastCameraStatusText = string.Empty;
        CameraStatusBadge.Background = new SolidColorBrush(Color.FromRgb(255, 233, 233));
        CameraStatusText.Foreground = new SolidColorBrush(Color.FromRgb(182, 50, 50));
        CameraStatusText.Text = "相机不可用";
        CameraPlaceholder.Visibility = Visibility.Visible;
        FooterText.Text = message;
    }

    private void UpdateCameraRuntimeInfo()
    {
        var info = _recordingBackend?.RuntimeStates.FirstOrDefault(state => state.CameraId == _recordingBackend.SelectedCameraId)
            ?? _recordingBackend?.RuntimeStates.FirstOrDefault(state => state.IsPrimary);
        if (info is null)
        {
            return;
        }
        var runtimeKey = $"{info.DisplayName}|{info.Width}|{info.Height}|{info.FramesPerSecond:0.###}|{info.ConnectionState}";
        if (string.Equals(_lastCameraRuntimeKey, runtimeKey, StringComparison.Ordinal))
        {
            return;
        }
        _lastCameraRuntimeKey = runtimeKey;
        CameraNameText.Text = info.DisplayName;
        ResolutionText.Text = $"{info.Width} × {info.Height}";
        FpsText.Text = $"{info.FramesPerSecond:0.#} fps";
    }

    private async Task RefreshRecentAsync()
    {
        if (_repository is null || !await _recentRefreshGate.WaitAsync(0))
        {
            return;
        }
        try
        {
            var records = (await _repository.QueryAsync(limit: 30, cancellationToken: _lifetime.Token))
                .Where(record => record.State is RecordingState.Completed or RecordingState.Imported or RecordingState.Failed)
                .Take(20)
                .ToArray();
            var deliveries = await _repository.GetLatestDeliveriesAsync(
                records.Select(record => record.Id).ToArray(),
                "excel",
                _lifetime.Token);
            var items = await Task.WhenAll(records.Select(record =>
                RecentRecordingItem.CreateAsync(
                    record,
                    deliveries.GetValueOrDefault(record.Id))));
            RecentItemsControl.ItemsSource = items;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _recentRefreshGate.Release();
        }
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                if (_syncDispatcher is not null)
                {
                    var processed = await _syncDispatcher.ProcessDueAsync(20, cancellationToken);
                    if (processed > 0)
                    {
                        await Dispatcher.BeginInvoke(() => SyncStatusText.Text = $"Excel：本轮处理 {processed} 条");
                    }
                }
                await RetryPendingVideoRenamesAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RetryPendingVideoRenamesAsync(CancellationToken cancellationToken)
    {
        if (_repository is null) return;
        var records = await _repository.QueryAsync(limit: 500, cancellationToken: cancellationToken);
        foreach (var record in records.Where(item => item.State == RecordingState.Completed && !string.IsNullOrWhiteSpace(item.VideoPath)))
        {
            var key = $"video-rename:{record.Id:D}";
            if (string.IsNullOrWhiteSpace(await _repository.GetMetadataAsync(key, cancellationToken))) continue;
            try
            {
                var renamed = RecordingFileRenameService.TryRenameLocalRecording(record, _settings.RecordingRoot);
                if (!string.IsNullOrWhiteSpace(renamed))
                {
                    record.VideoPath = renamed;
                    record.UpdatedAt = DateTimeOffset.Now;
                    await _repository.UpdateAsync(record, cancellationToken);
                    await _repository.SetMetadataAsync(key, string.Empty, cancellationToken);
                    await _repository.EnqueueDeliveryAsync(record.Id, "excel", cancellationToken);
                }
            }
            catch (IOException ex)
            {
                await _repository.SetMetadataAsync(key, ex.Message, cancellationToken);
            }
        }
    }

    private async void EmergencyStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null)
        {
            return;
        }
        await FlushIssueNoteAsync();
        await _coordinator.EmergencyStopAsync(_lifetime.Token);
        await RefreshRecentAsync();
        ScannerInput.Focus();
    }

    private async void SnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingBackend is null)
        {
            return;
        }
        try
        {
            var path = await _recordingBackend.TakeSnapshotAsync(_lifetime.Token);
            _lastSnapshotPaths.Add(path);
            if (_coordinator?.CurrentRecord is { } current && _repository is not null)
            {
                current.Snapshots = [.. current.Snapshots, path];
                current.UpdatedAt = DateTimeOffset.Now;
                await _repository.UpdateAsync(current, _lifetime.Token);
            }
            FooterText.Text = "照片已保存，可点击画面下方“查看照片”";
            Speak("拍照已保存");
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
        }
    }

    private async void SnapshotAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingBackend is null) return;
        try
        {
            var paths = await _recordingBackend.TakeAllSnapshotsAsync(_lifetime.Token);
            _lastSnapshotPaths.AddRange(paths);
            if (_coordinator?.CurrentRecord is { } current && _repository is not null)
            {
                current.Snapshots = [.. current.Snapshots, .. paths];
                current.UpdatedAt = DateTimeOffset.Now;
                await _repository.UpdateAsync(current, _lifetime.Token);
            }
            FooterText.Text = $"已保存 {paths.Count} 张机位照片，可点击“查看照片”";
            Speak("全部机位拍照已保存");
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
        }
    }

    private async void RotateLeftButton_OnClick(object sender, RoutedEventArgs e) => await RunCameraActionAsync(token => _recordingBackend!.RotateLeftAsync(token));
    private async void RotateRightButton_OnClick(object sender, RoutedEventArgs e) => await RunCameraActionAsync(token => _recordingBackend!.RotateRightAsync(token));
    private async void MirrorButton_OnClick(object sender, RoutedEventArgs e) => await RunCameraActionAsync(token => _recordingBackend!.ToggleMirrorAsync(token));

    private async Task RunCameraActionAsync(Func<CancellationToken, Task> action)
    {
        if (_recordingBackend is null)
        {
            return;
        }
        try
        {
            await action(_lifetime.Token);
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
            Speak(ex.Message);
        }
        ScannerInput.Focus();
    }

    private void ViewSnapshotsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var current = _coordinator?.CurrentRecord;
        var paths = current is not null
            ? current.Snapshots
            : _lastSnapshotPaths.Count > 0
                ? _lastSnapshotPaths
                : FindRecentSnapshotFiles();
        var trackingNo = current?.TrackingNo ?? (_lastSnapshotPaths.Count > 0 ? _displayedTrackingNo : string.Empty);
        PhotoGalleryWindow.TryShow(this, trackingNo, paths);
        ScannerInput.Focus();
    }

    private IReadOnlyList<string> FindRecentSnapshotFiles()
    {
        try
        {
            var root = Path.Combine(_settings.RecordingRoot, "Snapshots");
            if (!Directory.Exists(root)) return [];
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => new[] { ".jpg", ".jpeg", ".png" }
                    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(200)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterText.Text = "照片目录暂时无法读取";
            return [];
        }
    }

    private void FullScreenButton_OnClick(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    private void SetFullScreen(bool enabled)
    {
        _fullScreen = enabled;
        HeaderRow.Height = enabled ? new GridLength(0) : new GridLength(74);
        CameraToolbarRow.Height = enabled ? new GridLength(0) : new GridLength(56);
        FooterRow.Height = enabled ? new GridLength(0) : new GridLength(34);
        BottomControlsRow.Height = enabled ? new GridLength(0) : new GridLength(138);
        ResultsColumn.Width = enabled ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ResultsColumn.MinWidth = enabled ? 0 : 390;
        PreviewColumn.Width = new GridLength(enabled ? 1 : 2.35, GridUnitType.Star);
        WindowStyle = enabled ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = enabled ? WindowState.Maximized : WindowState.Normal;
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullScreen)
        {
            SetFullScreen(false);
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ApplyNativeWindowAppearance()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }
        var handle = new WindowInteropHelper(this).Handle;
        var cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        var darkMode = 0;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    private async void AutoFocusButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = SelectedCameraProfile();
        profile.AutoFocus = !profile.AutoFocus;
        AutoFocusButton.Content = profile.AutoFocus ? "自动聚焦：开" : "自动聚焦：关";
        if (_recordingBackend is not null)
        {
            await _recordingBackend.SetAutoFocusAsync(profile.AutoFocus, _lifetime.Token);
        }
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        ScannerInput.Focus();
    }

    private async void FocusOnceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingBackend is not null)
        {
            await _recordingBackend.FocusOnceAsync(_lifetime.Token);
            FooterText.Text = "已触发相机自动聚焦";
        }
        ScannerInput.Focus();
    }

    private void ImageAdjustButton_OnClick(object sender, RoutedEventArgs e) => ImageAdjustPopup.IsOpen = true;

    private void ImageControlSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _recordingBackend is null)
        {
            return;
        }
        _imageControlTimer.Stop();
        _imageControlTimer.Start();
    }

    private async void OnImageControlTimer(object? sender, EventArgs e)
    {
        _imageControlTimer.Stop();
        await ApplyCurrentImageControlsAsync(saveSettings: false);
    }

    private async void ImageAdjustPopup_OnClosed(object? sender, EventArgs e)
    {
        _imageControlTimer.Stop();
        await ApplyCurrentImageControlsAsync(saveSettings: true);
        ScannerInput.Focus();
    }

    private async Task ApplyCurrentImageControlsAsync(bool saveSettings)
    {
        var profile = SelectedCameraProfile();
        profile.Brightness = BrightnessSlider.Value;
        profile.Contrast = ContrastSlider.Value;
        profile.Sharpness = SharpnessSlider.Value;
        profile.Saturation = SaturationSlider.Value;
        if (_recordingBackend is not null)
        {
            await _recordingBackend.ApplyImageControlsAsync(
                BrightnessSlider.Value,
                ContrastSlider.Value,
                SharpnessSlider.Value,
                SaturationSlider.Value,
                _lifetime.Token);
        }
        if (saveSettings)
        {
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
    }

    private void RestoreImageDefaults_OnClick(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value = 50;
        ContrastSlider.Value = 50;
        SharpnessSlider.Value = 50;
        SaturationSlider.Value = 50;
    }

    private async void FaceZoomButton_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.FaceZoomEnabled = !_settings.FaceZoomEnabled;
        foreach (var image in _cameraPreviewImages.Values)
        {
            image.Stretch = _settings.FaceZoomEnabled ? Stretch.UniformToFill : Stretch.Uniform;
        }
        FaceZoomButton.Content = _settings.FaceZoomEnabled ? "面单放大：开" : "面单放大：关";
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        ScannerInput.Focus();
    }

    private async void VoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.VoiceEnabled = !_settings.VoiceEnabled;
        ApplyVoiceVisual();
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        ScannerInput.Focus();
    }

    private async void UnpackingModeButton_OnClick(object sender, RoutedEventArgs e) => await SetWorkflowAsync(WorkflowMode.Unpacking);
    private async void PackingModeButton_OnClick(object sender, RoutedEventArgs e) => await SetWorkflowAsync(WorkflowMode.Packing);

    private async Task SetWorkflowAsync(WorkflowMode mode)
    {
        if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
        {
            FooterText.Text = "录像过程中不能切换拆包/打包模式";
            return;
        }
        _settings.Workflow = mode;
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        ApplyWorkflowVisuals();
        OutputPathText.Text = GetCurrentOutputPath();
        ScannerInput.Focus();
    }

    private void HistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_repository is not null)
        {
            var history = new HistoryWindow(_repository, _settings) { Owner = this };
            history.Closed += async (_, _) => await RefreshRecentAsync();
            history.Show();
        }
        ScannerInput.Focus();
    }

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs e) => await OpenSettingsAsync(false);

    private async Task OpenSettingsAsync(bool showCameraTab)
    {
        var previousRecordingRoot = _settings.RecordingRoot;
        var previousExcelPath = _settings.ExcelWorkbookPath;
        var dialog = new SettingsWindow(
            _settings,
            App.HikvisionDiscovery,
            App.IpcDiscovery,
            this,
            App.StoragePoolMonitor,
            async (candidate, migration, cancellationToken) =>
            {
                var workspace = await new PortableRecordCatalog(candidate.RecordingRoot)
                    .EnsureWorkspaceAsync(
                        string.Equals(previousRecordingRoot, candidate.RecordingRoot, StringComparison.OrdinalIgnoreCase)
                            ? candidate.Setup.WorkspaceId
                            : null,
                        cancellationToken);
                candidate.Setup.WorkspaceId = workspace.WorkspaceId;
                candidate.Setup.Version = SetupState.CurrentVersion;
                candidate.Setup.CompletedAt ??= DateTimeOffset.Now;
                candidate.Setup.ExcelSkipped = string.IsNullOrWhiteSpace(candidate.ExcelWorkbookPath);
                await RebaseMigratedRecordPathsAsync(migration, cancellationToken);
                await _settingsStore.SaveAsync(candidate, cancellationToken);
            },
            showCameraTab) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SavedSettings is null)
        {
            ScannerInput.Focus();
            return;
        }
        _settings = dialog.SavedSettings;
        var workspace = await new PortableRecordCatalog(_settings.RecordingRoot)
            .EnsureWorkspaceAsync(
                string.Equals(previousRecordingRoot, _settings.RecordingRoot, StringComparison.OrdinalIgnoreCase)
                    ? _settings.Setup.WorkspaceId
                    : null,
                _lifetime.Token);
        _settings.Setup.WorkspaceId = workspace.WorkspaceId;
        _settings.Setup.Version = SetupState.CurrentVersion;
        _settings.Setup.CompletedAt ??= DateTimeOffset.Now;
        _settings.Setup.ExcelSkipped = string.IsNullOrWhiteSpace(_settings.ExcelWorkbookPath);
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        await App.ApplyTelemetryAsync(_settings, _lifetime.Token);
        _storageOptions!.RecordingRoot = _settings.RecordingRoot;
        _storageOptions.StoragePool = _settings.StoragePool;
        _storageOptions.MaximumRecordingMinutes = _settings.MaximumRecordingMinutes;
        _coordinator?.UpdateScannerProfile(_settings.Scanner);
        RebuildStationRouter();
        _syncDispatcher = new SyncDispatcher(_repository!, [new ExcelConnector(CreateExcelOptions())], new SystemClock());
        if (string.IsNullOrWhiteSpace(previousExcelPath) &&
            !string.IsNullOrWhiteSpace(_settings.ExcelWorkbookPath) &&
            MessageBox.Show(
                this,
                "是否把数据库中尚未同步的历史完成记录加入 Excel 同步队列？",
                "补同步历史记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var record in await _repository!.QueryAsync(limit: 2000, cancellationToken: _lifetime.Token))
            {
                if (record.State is RecordingState.Completed or RecordingState.Imported or RecordingState.Collected)
                {
                    await _repository.EnqueueDeliveryAsync(record.Id, "excel", _lifetime.Token);
                }
            }
        }
        ApplySettingsVisuals();
        if (_recordingBackend is not null)
        {
            try
            {
                if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
                {
                    FooterText.Text = "录像进行中，新的机位方案将在下次启动后生效";
                }
                else
                {
                    await RecreateCameraBackendAsync();
                }
            }
            catch (Exception ex)
            {
                ShowCameraError(ex.Message);
            }
        }
        ScannerInput.Focus();
    }

    private async Task RebaseMigratedRecordPathsAsync(
        RecordingRootMigrationResult migration,
        CancellationToken cancellationToken)
    {
        if (_repository is null || _storageOptions is null)
        {
            throw new InvalidOperationException("记录数据库尚未初始化，已保留旧目录中的录像文件");
        }

        var verifiedPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var receipt in migration.VerifiedFiles)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(migration.SourceRoot, receipt.RelativePath));
            var targetPath = Path.GetFullPath(Path.Combine(migration.TargetRoot, receipt.RelativePath));
            verifiedPathMap[sourcePath] = targetPath;
        }
        var backup = new WorkspaceRecoveryService(_repository, _storageOptions);
        await backup.BackupDatabaseForMigrationAsync(cancellationToken);
        await new RecordingRootMigrationService().VerifyAsync(migration, cancellationToken);
        await _repository.RebaseOwnedPathsAsync(verifiedPathMap, cancellationToken);
    }

    private async Task RecreateCameraBackendAsync()
    {
        if (_repository is null || _recordingBackend is null)
        {
            return;
        }
        DetachRecordingBackend(_recordingBackend);
        await _recordingBackend.DisposeAsync();
        _recordingBackend = CreateRecordingBackend(_settings.CameraRig);
        _coordinator = new RecordingCoordinator(
            _repository,
            _recordingBackend,
            new NullEventPublisher(),
            new SystemClock(),
            _settings.Scanner);
        _coordinator.StateChanged += Coordinator_OnStateChanged;
        RebuildStationRouter();
        PopulateCameraSourceSelector();
        BuildCameraPreviewGrid();
        if (_settings.ShowLivePreview)
        {
            await _recordingBackend.StartPreviewAsync(_lifetime.Token);
        }
        UpdateCameraRuntimeInfo();
    }

    internal async Task<CameraRigPerformanceResult> TestCandidateCameraRigAsync(
        CameraRigOptions candidate,
        IProgress<CameraPerformanceProgress>? progress = null,
        UnpackVision.Core.Recording.StoragePoolOptions? storagePool = null)
    {
        if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
        {
            throw new InvalidOperationException("录像过程中不能执行机位性能测试");
        }
        if (_recordingBackend is null || _storageOptions is null)
        {
            throw new InvalidOperationException("相机后端尚未初始化");
        }

        DetachRecordingBackend(_recordingBackend);
        await _recordingBackend.DisposeAsync();
        MultiCameraRecordingBackend? testBackend = null;
        try
        {
            var testStorage = new StorageOptions
            {
                DatabasePath = _storageOptions.DatabasePath,
                RecordingRoot = storagePool?.Targets
                    .Where(target => target.Enabled)
                    .OrderBy(target => target.Priority)
                    .Select(target => target.RootPath)
                    .FirstOrDefault() ?? _storageOptions.RecordingRoot,
                StoragePool = storagePool ?? _storageOptions.StoragePool,
                MaximumRecordingMinutes = _storageOptions.MaximumRecordingMinutes
            };
            testBackend = new MultiCameraRecordingBackend(testStorage, candidate);
            return await testBackend.TestPerformanceAsync(progress, _lifetime.Token);
        }
        finally
        {
            if (testBackend is not null)
            {
                await testBackend.DisposeAsync();
            }
            _recordingBackend = CreateRecordingBackend(_settings.CameraRig);
            _coordinator = new RecordingCoordinator(
                _repository!,
                _recordingBackend,
                new NullEventPublisher(),
                new SystemClock(),
                _settings.Scanner);
            _coordinator.StateChanged += Coordinator_OnStateChanged;
            RebuildStationRouter();
            BuildCameraPreviewGrid();
            if (_settings.ShowLivePreview)
            {
                await _recordingBackend.StartPreviewAsync(_lifetime.Token);
            }
        }
    }

    async Task ICameraConfigurationPreviewHost.RunCameraConfigurationPreviewAsync(
        CameraProfile profile,
        Action<CameraConfigurationPreviewFrame> onFrame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(onFrame);
        await _cameraConfigurationPreviewGate.WaitAsync(cancellationToken);
        MultiCameraRecordingBackend? previewBackend = null;
        var resumeStationPreview = false;
        try
        {
            if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
            {
                throw new InvalidOperationException("录像过程中不能切换配置预览");
            }
            if (_recordingBackend is null || _storageOptions is null)
            {
                throw new InvalidOperationException("相机后端尚未初始化");
            }

            _cameraConfigurationPreviewActive = true;
            resumeStationPreview = _recordingBackend.IsPreviewing && _settings.ShowLivePreview;
            await _recordingBackend.StopPreviewAsync(cancellationToken);

            var candidate = CloneForConfigurationPreview(profile);
            var rig = new CameraRigOptions
            {
                Mode = CameraRigMode.SingleCamera,
                Cameras = [candidate],
                CompositeRecordingEnabled = false,
                CompositeWidth = 1920,
                CompositeHeight = 1080,
                CompositeFramesPerSecond = 15
            };
            previewBackend = new MultiCameraRecordingBackend(
                new StorageOptions
                {
                    DatabasePath = _storageOptions.DatabasePath,
                    RecordingRoot = _storageOptions.RecordingRoot,
                    StoragePool = _storageOptions.StoragePool,
                    MaximumRecordingMinutes = _storageOptions.MaximumRecordingMinutes
                },
                rig);
            previewBackend.PreviewFrameReady += PreviewFrameReady;
            await previewBackend.StartPreviewAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            void PreviewFrameReady(object? sender, MultiCameraPreviewFrameEventArgs args)
            {
                var runtime = previewBackend?.RuntimeStates.FirstOrDefault();
                onFrame(new CameraConfigurationPreviewFrame(
                    args.JpegBytes,
                    args.CapturedAt,
                    runtime?.Width ?? candidate.Width,
                    runtime?.Height ?? candidate.Height,
                    runtime?.FramesPerSecond ?? candidate.FramesPerSecond));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (previewBackend is not null)
            {
                await previewBackend.DisposeAsync();
            }
            _cameraConfigurationPreviewActive = false;
            if (resumeStationPreview && _recordingBackend is not null && !_lifetime.IsCancellationRequested)
            {
                try
                {
                    await _recordingBackend.StartPreviewAsync(_lifetime.Token);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException)
                {
                    ShowCameraError($"恢复主页预览失败：{exception.Message}");
                }
            }
            _cameraConfigurationPreviewGate.Release();
        }
    }

    private static CameraProfile CloneForConfigurationPreview(CameraProfile camera) => new()
    {
        Id = camera.Id,
        DisplayName = camera.DisplayName,
        Enabled = true,
        IsPrimary = true,
        SortOrder = 0,
        SourceType = camera.SourceType,
        WindowsSymbolicLink = camera.WindowsSymbolicLink,
        LegacyCameraIndex = camera.LegacyCameraIndex,
        AutoSelectBestCamera = camera.AutoSelectBestCamera,
        Width = camera.Width,
        Height = camera.Height,
        FramesPerSecond = camera.FramesPerSecond,
        Codec = camera.Codec,
        RotationQuarterTurns = camera.RotationQuarterTurns,
        Mirror = camera.Mirror,
        Brightness = camera.Brightness,
        Contrast = camera.Contrast,
        Sharpness = camera.Sharpness,
        Saturation = camera.Saturation,
        AutoFocus = camera.AutoFocus,
        NetworkStreamUrl = camera.NetworkStreamUrl,
        NetworkUsername = camera.NetworkUsername,
        NetworkPasswordProtected = camera.NetworkPasswordProtected,
        HikvisionHost = camera.HikvisionHost,
        HikvisionHttpPort = camera.HikvisionHttpPort,
        HikvisionRtspPort = camera.HikvisionRtspPort,
        HikvisionChannel = camera.HikvisionChannel,
        HikvisionSubStream = camera.HikvisionSubStream
    };

    private void PopulateCameraSourceSelector()
    {
        _updatingCameraSourceSelector = true;
        CameraSourceSelector.Items.Clear();
        foreach (var choice in CameraQuickSelectionBuilder.Build(
                     _settings.CameraRig,
                     WindowsCameraDiscovery.Enumerate()))
        {
            CameraSourceSelector.Items.Add(choice);
        }
        CameraSourceSelector.DisplayMemberPath = nameof(CameraQuickSelection.Label);
        SelectCurrentCameraSourceChoice();
        _updatingCameraSourceSelector = false;
    }

    private void SelectCurrentCameraSourceChoice()
    {
        _updatingCameraSourceSelector = true;
        var choices = CameraSourceSelector.Items.Cast<CameraQuickSelection>();
        var primary = _settings.CameraRig.PrimaryCamera;
        var choice = CameraQuickSelectionBuilder.UsesLocalDevicePicker(_settings.CameraRig) && primary is not null
            ? choices.FirstOrDefault(item => item.IsLocalDevice &&
                ((!string.IsNullOrWhiteSpace(primary.WindowsSymbolicLink) &&
                  string.Equals(item.WindowsSymbolicLink, primary.WindowsSymbolicLink, StringComparison.OrdinalIgnoreCase)) ||
                 (string.IsNullOrWhiteSpace(primary.WindowsSymbolicLink) &&
                  item.WindowsCameraIndex == primary.LegacyCameraIndex)))
              ?? choices.FirstOrDefault(item => item.IsAutomaticLocalChoice &&
                  primary.SourceType == CameraSourceType.AutoLocal)
              ?? choices.FirstOrDefault()
            : choices.FirstOrDefault(item =>
                string.Equals(item.CameraId, _recordingBackend?.SelectedCameraId, StringComparison.OrdinalIgnoreCase))
              ?? choices.FirstOrDefault();
        CameraSourceSelector.SelectedItem = choice;
        _updatingCameraSourceSelector = false;
    }

    private async void CameraSourceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCameraSourceSelector || !IsLoaded ||
            CameraSourceSelector.SelectedItem is not CameraQuickSelection choice || _recordingBackend is null)
        {
            return;
        }

        var recordingActive = _coordinator?.State is
            RecordingState.Recording or RecordingState.Starting or RecordingState.Saving;
        var primary = _settings.CameraRig.PrimaryCamera;
        var switchingLocalDevice = choice.IsLocalDevice && primary is not null &&
            (!string.Equals(primary.WindowsSymbolicLink, choice.WindowsSymbolicLink, StringComparison.OrdinalIgnoreCase) ||
             primary.LegacyCameraIndex != choice.WindowsCameraIndex ||
             primary.SourceType != CameraSourceType.WindowsCamera);
        var switchingAutomaticLocal = choice.IsAutomaticLocalChoice && primary?.SourceType != CameraSourceType.AutoLocal;
        var switchingSingleCamera = _settings.CameraRig.Mode == CameraRigMode.SingleCamera &&
            (switchingLocalDevice || switchingAutomaticLocal ||
             !string.Equals(choice.CameraId, _recordingBackend.SelectedCameraId, StringComparison.OrdinalIgnoreCase));
        if (recordingActive && switchingSingleCamera)
        {
            SelectCurrentCameraSourceChoice();
            FooterText.Text = "录像过程中不能切换摄像头，请先停止并保存当前录像";
            return;
        }
        if (recordingActive)
        {
            FooterText.Text = "已切换查看机位；录像配置在录制结束后才能修改";
        }

        if (string.IsNullOrWhiteSpace(choice.CameraId))
        {
            return;
        }

        if (switchingSingleCamera)
        {
            try
            {
                if (choice.IsLocalDevice)
                {
                    ApplyLocalCameraQuickSelection(primary, choice);
                }
                else if (choice.IsAutomaticLocalChoice)
                {
                    ApplyAutomaticLocalQuickSelection(primary);
                }
                else if (!_settings.CameraRig.TrySelectSingleCamera(choice.CameraId))
                {
                    throw new InvalidOperationException("所选摄像头配置不可用");
                }
                await _settingsStore.SaveAsync(_settings, _lifetime.Token);
                await RecreateCameraBackendAsync();
                FooterText.Text = $"已切换当前摄像头：{choice.Label}";
            }
            catch (Exception ex)
            {
                ShowCameraError(ex.Message);
            }
            return;
        }

        _recordingBackend.SelectCamera(choice.CameraId);
        if (!_cameraPreviewTiles.ContainsKey(choice.CameraId))
        {
            _settings.PreviewLayout.CameraIds = CameraPreviewLayoutPlanner.Assign(
                _settings.PreviewLayout.CameraIds,
                _settings.PreviewLayout.ViewCount,
                0,
                choice.CameraId).ToList();
            _expandedCameraId = null;
            BuildCameraPreviewGrid();
            await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        }
        if (!string.Equals(_expandedCameraId, choice.CameraId, StringComparison.OrdinalIgnoreCase))
        {
            ToggleExpandedCamera(choice.CameraId);
        }
        UpdateCameraRuntimeInfo();
        FooterText.Text = $"当前画面参数作用于 {choice.Label}";
    }

    private static void ApplyLocalCameraQuickSelection(CameraProfile? primary, CameraQuickSelection choice)
    {
        if (primary is null || choice.WindowsCameraIndex is null || string.IsNullOrWhiteSpace(choice.WindowsSymbolicLink))
        {
            throw new InvalidOperationException("所选本地摄像头已不可用，请在设置中刷新设备后重试");
        }

        primary.SourceType = CameraSourceType.WindowsCamera;
        primary.LegacyCameraIndex = choice.WindowsCameraIndex.Value;
        primary.WindowsSymbolicLink = choice.WindowsSymbolicLink;
        primary.AutoSelectBestCamera = false;
    }

    private static void ApplyAutomaticLocalQuickSelection(CameraProfile? primary)
    {
        if (primary is null)
        {
            throw new InvalidOperationException("当前没有可用主机位");
        }

        primary.SourceType = CameraSourceType.AutoLocal;
        primary.AutoSelectBestCamera = true;
        primary.WindowsSymbolicLink = string.Empty;
    }

    private void RecentPlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecentRecordingItem item)
        {
            PlayRecording(item);
        }
    }

    private void RecentOpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentRecordingItem item ||
            string.IsNullOrWhiteSpace(item.Record.VideoPath) || !File.Exists(item.Record.VideoPath))
        {
            FooterText.Text = "录像文件不存在";
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Record.VideoPath}\"") { UseShellExecute = true });
    }

    private void RecentPhotosButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecentRecordingItem item)
        {
            PhotoGalleryWindow.TryShow(this, item.TrackingNo, item.Record.Snapshots);
        }
        ScannerInput.Focus();
    }

    private void PlayRecording(RecentRecordingItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Record.VideoPath) || !File.Exists(item.Record.VideoPath))
        {
            FooterText.Text = "录像文件不存在";
            return;
        }
        new VideoPlayerWindow(item.Record) { Owner = this }.Show();
    }

    private void ApplySettingsVisuals()
    {
        OutputPathText.Text = GetCurrentOutputPath();
        UpdatePreviewLayoutButtons();
        var profile = SelectedCameraProfile();
        AutoFocusButton.Content = profile.AutoFocus ? "自动聚焦：开" : "自动聚焦：关";
        ApplyVoiceVisual();
        FaceZoomButton.Content = _settings.FaceZoomEnabled ? "面单放大：开" : "面单放大：关";
        foreach (var image in _cameraPreviewImages.Values)
        {
            image.Stretch = _settings.FaceZoomEnabled ? Stretch.UniformToFill : Stretch.Uniform;
        }
        BrightnessSlider.Value = profile.Brightness;
        ContrastSlider.Value = profile.Contrast;
        SharpnessSlider.Value = profile.Sharpness;
        SaturationSlider.Value = profile.Saturation;
        ResolutionText.Text = $"{profile.Width} × {profile.Height}";
        FpsText.Text = $"{profile.FramesPerSecond:0.#} fps";
        if (CameraSourceSelector.Items.Count > 0)
        {
            SelectCurrentCameraSourceChoice();
        }
        ApplyWorkflowVisuals();
        QuickIssueTagsControl.ItemsSource = _settings.IssueTags.Where(item => item.Enabled).OrderBy(item => item.SortOrder).ToArray();
        LabelDesigner.ConfigureIssueTags(_settings.IssueTags);
    }

    private CameraProfile SelectedCameraProfile() => _settings.CameraRig.EnabledCameras
        .FirstOrDefault(camera => string.Equals(camera.Id, _recordingBackend?.SelectedCameraId, StringComparison.OrdinalIgnoreCase))
        ?? _settings.CameraRig.PrimaryCamera
        ?? _settings.CameraRig.EnabledCameras.First();

    private void RecordingPageButton_OnClick(object sender, RoutedEventArgs e) => ShowDesignerPage(false);
    private void BarcodePageButton_OnClick(object sender, RoutedEventArgs e) => ShowDesignerPage(true);

    private void ShowDesignerPage(bool show)
    {
        _designerPageVisible = show;
        CameraToolbar.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RecordingWorkspace.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        LabelDesigner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RecordingPageButton.Background = show ? new SolidColorBrush(Color.FromRgb(250, 251, 252)) : (Brush)FindResource("PrimaryBrush");
        RecordingPageButton.Foreground = show ? Brushes.Black : Brushes.White;
        BarcodePageButton.Background = show ? (Brush)FindResource("PrimaryBrush") : new SolidColorBrush(Color.FromRgb(250, 251, 252));
        BarcodePageButton.Foreground = show ? Brushes.White : Brushes.Black;
        if (!show)
        {
            ScannerInput.Focus();
        }
    }

    private void ApplyWorkflowVisuals()
    {
        var primary = (Brush)FindResource("PrimaryBrush");
        var secondary = new SolidColorBrush(Color.FromRgb(250, 251, 252));
        var secondaryText = new SolidColorBrush(Color.FromRgb(78, 78, 84));
        var unpacking = _settings.Workflow == WorkflowMode.Unpacking;
        UnpackingModeButton.Background = unpacking ? primary : secondary;
        UnpackingModeButton.Foreground = unpacking ? Brushes.White : secondaryText;
        PackingModeButton.Background = unpacking ? secondary : primary;
        PackingModeButton.Foreground = unpacking ? secondaryText : Brushes.White;
    }

    private void ApplyVoiceVisual()
    {
        VoiceButton.Content = _settings.VoiceEnabled ? "\uE767" : "\uE74F";
        VoiceButton.ToolTip = _settings.VoiceEnabled ? "语音播报：开" : "语音播报：关";
        VoiceButton.Foreground = _settings.VoiceEnabled
            ? (Brush)FindResource("PrimaryBrush")
            : new SolidColorBrush(Color.FromRgb(110, 110, 115));
    }

    private static void AnimateIn(UIElement element)
    {
        element.Opacity = 0;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(
            0,
            1,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private void UpdateCurrentTrackingBarcode(string trackingNo)
    {
        var bytes = BarcodePresentationService.CreateCode128Png(trackingNo);
        CurrentTrackingBarcodeImage.Source = UiImage.FromBytes(bytes);
        CurrentTrackingBarcodeHintText.Text = $"再次扫描当前单号 {trackingNo} 结束录像";
    }

    private string GetCurrentOutputPath() => Path.Combine(
        _settings.RecordingRoot,
        _settings.Workflow == WorkflowMode.Unpacking ? "Unpacking" : "Packing");

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private ExcelConnectorOptions CreateExcelOptions() => new()
    {
        WorkbookPath = _settings.ExcelWorkbookPath
    };

    private MultiCameraRecordingBackend CreateRecordingBackend(CameraRigOptions rig)
    {
        var backend = new MultiCameraRecordingBackend(_storageOptions!, rig);
        backend.PreviewFrameReady += RecordingBackend_OnPreviewFrameReady;
        backend.CameraStateChanged += RecordingBackend_OnCameraStateChanged;
        backend.StorageWarningRaised += RecordingBackend_OnStorageWarningRaised;
        backend.PrimaryRecordingFailed += RecordingBackend_OnPrimaryRecordingFailed;
        return backend;
    }

    private void DetachRecordingBackend(MultiCameraRecordingBackend backend)
    {
        backend.PreviewFrameReady -= RecordingBackend_OnPreviewFrameReady;
        backend.CameraStateChanged -= RecordingBackend_OnCameraStateChanged;
        backend.StorageWarningRaised -= RecordingBackend_OnStorageWarningRaised;
        backend.PrimaryRecordingFailed -= RecordingBackend_OnPrimaryRecordingFailed;
    }

    private void Speak(string message)
    {
        if (!_settings.VoiceEnabled)
        {
            return;
        }
        _speech.Speak(message, _settings.VoiceVolume);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }
        await FlushIssueNoteAsync();
        if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
        {
            var answer = MessageBox.Show(
                this,
                "仍有录像正在进行。是否先停止并保存？",
                "确认退出",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
            await _coordinator.EmergencyStopAsync();
        }

        _shutdownStarted = true;
        App.Updates.StatusChanged -= Updates_OnStatusChanged;
        App.Updates.UpdateReady -= Updates_OnUpdateReady;
        _stationStateTimer.Stop();
        if (_desktopCommandListener is not null)
        {
            await _desktopCommandListener.DisposeAsync();
        }
        _lifetime.Cancel();
        _rawScanner?.Dispose();
        if (_recordingBackend is not null)
        {
            DetachRecordingBackend(_recordingBackend);
            await _recordingBackend.DisposeAsync();
        }
        _speech.Dispose();
        _lifetime.Dispose();
        _allowClose = true;
        Close();
    }

}
