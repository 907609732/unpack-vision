using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<RecentRecordingItem> _recentItems = [];
    private readonly DispatcherTimer _recordingTimer;
    private readonly DispatcherTimer _imageControlTimer;
    private readonly LoudSpeechService _speech = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LocalSettingsStore _settingsStore = new();
    private LocalSettings _settings = new();
    private StorageOptions? _storageOptions;
    private IScanRecordRepository? _repository;
    private OpenCvRecordingBackend? _recordingBackend;
    private RecordingCoordinator? _coordinator;
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
    private int _previewUpdatePending;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeWindowAppearance();
        RecentItemsControl.ItemsSource = _recentItems;
        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnRecordingTimer, Dispatcher);
        _imageControlTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, OnImageControlTimer, Dispatcher);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            PopulateCameraSourceSelector();
            ApplySettingsVisuals();
            _storageOptions = new StorageOptions { RecordingRoot = _settings.RecordingRoot };
            var excelOptions = CreateExcelOptions();

            _repository = new SqliteScanRecordRepository(_storageOptions);
            await _repository.InitializeAsync(_lifetime.Token);
            var interrupted = await new InterruptedRecordingRecovery(_repository, new SystemClock())
                .MarkInterruptedAsync(_lifetime.Token);

            _recordingBackend = new OpenCvRecordingBackend(_storageOptions, _settings.Camera);
            _recordingBackend.PreviewFrameReady += RecordingBackend_OnPreviewFrameReady;
            _recordingBackend.CameraError += RecordingBackend_OnCameraError;
            _coordinator = new RecordingCoordinator(
                _repository,
                _recordingBackend,
                new NullEventPublisher(),
                new SystemClock(),
                _settings.Scanner);
            _coordinator.StateChanged += Coordinator_OnStateChanged;

            _rawScanner = new RawInputScannerCapture(this, () => _settings.Scanner);
            _rawScanner.BarcodeScanned += RawScanner_OnBarcodeScanned;
            _syncDispatcher = new SyncDispatcher(_repository, [new ExcelConnector(excelOptions)], new SystemClock());
            _ = RunSyncLoopAsync(_lifetime.Token);

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
        await ProcessBarcodeAsync(value, "焦点输入框");
    }

    private async void RawScanner_OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e) =>
        await ProcessBarcodeAsync(e.Value, e.DeviceName);

    private async Task ProcessBarcodeAsync(string value, string deviceName)
    {
        if (_coordinator is null || string.IsNullOrWhiteSpace(value))
        {
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

    private void ShowRecordingUi(ScanRecord record)
    {
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
    }

    private void ShowIdleUi(string message)
    {
        CurrentStateText.Text = "等待扫码";
        StateDot.Fill = new SolidColorBrush(Color.FromRgb(67, 181, 129));
        RecordingBadge.Visibility = Visibility.Collapsed;
        WatermarkPreview.Visibility = Visibility.Collapsed;
        RecordingActionPanel.Visibility = Visibility.Collapsed;
        IdleActionPanel.Visibility = Visibility.Visible;
        CurrentTrackingBarcodeImage.Source = null;
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
        WatermarkTrackingText.Text = $"快递单号：{_coordinator?.CurrentRecord?.TrackingNo ?? ""}";
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

    private void RecordingBackend_OnPreviewFrameReady(object? sender, PreviewFrameEventArgs e)
    {
        if (Interlocked.Exchange(ref _previewUpdatePending, 1) == 1 || _lifetime.IsCancellationRequested)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            try
            {
                CameraPreviewImage.Source = UiImage.FromBytes(e.JpegBytes);
                CameraPlaceholder.Visibility = Visibility.Collapsed;
                CameraStatusBadge.Background = new SolidColorBrush(Color.FromRgb(232, 248, 239));
                CameraStatusText.Foreground = new SolidColorBrush(Color.FromRgb(32, 126, 78));
                CameraStatusText.Text = _recordingBackend?.IsRecording == true ? "相机正常 · 正在录像" : "相机正常 · 实时预览";
                UpdateCameraRuntimeInfo();
            }
            finally
            {
                Interlocked.Exchange(ref _previewUpdatePending, 0);
            }
        });
    }

    private void RecordingBackend_OnCameraError(object? sender, CameraErrorEventArgs e) =>
        Dispatcher.BeginInvoke(() => ShowCameraError(e.Error.Message));

    private void ShowCameraError(string message)
    {
        CameraStatusBadge.Background = new SolidColorBrush(Color.FromRgb(255, 233, 233));
        CameraStatusText.Foreground = new SolidColorBrush(Color.FromRgb(182, 50, 50));
        CameraStatusText.Text = "相机不可用";
        CameraPlaceholder.Visibility = Visibility.Visible;
        FooterText.Text = message;
    }

    private void UpdateCameraRuntimeInfo()
    {
        var info = _recordingBackend?.RuntimeInfo;
        if (info is null)
        {
            return;
        }
        CameraNameText.Text = info.DisplayName;
        ResolutionText.Text = $"{info.Width} × {info.Height}";
        FpsText.Text = $"{info.FramesPerSecond:0.#} fps";
    }

    private async Task RefreshRecentAsync()
    {
        if (_repository is null)
        {
            return;
        }
        var records = (await _repository.QueryAsync(limit: 30, cancellationToken: _lifetime.Token))
            .Where(record => record.State is RecordingState.Completed or RecordingState.Imported or RecordingState.Failed)
            .Take(20);
        _recentItems.Clear();
        foreach (var record in records)
        {
            var delivery = await _repository.GetDeliveryAsync(record.Id, "excel", _lifetime.Token);
            _recentItems.Add(await RecentRecordingItem.CreateAsync(record, delivery));
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
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void EmergencyStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_coordinator is null)
        {
            return;
        }
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
            if (_coordinator?.CurrentRecord is { } current && _repository is not null)
            {
                current.Snapshots = [.. current.Snapshots, path];
                current.UpdatedAt = DateTimeOffset.Now;
                await _repository.UpdateAsync(current, _lifetime.Token);
            }
            FooterText.Text = $"照片已保存：{path}";
            Speak("拍照已保存");
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
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
            Speak(ex.Message);
        }
        ScannerInput.Focus();
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
        _settings.Camera.AutoFocus = !_settings.Camera.AutoFocus;
        AutoFocusButton.Content = _settings.Camera.AutoFocus ? "自动聚焦：开" : "自动聚焦：关";
        if (_recordingBackend is not null)
        {
            await _recordingBackend.SetAutoFocusAsync(_settings.Camera.AutoFocus, _lifetime.Token);
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
        _settings.Camera.Brightness = BrightnessSlider.Value;
        _settings.Camera.Contrast = ContrastSlider.Value;
        _settings.Camera.Sharpness = SharpnessSlider.Value;
        _settings.Camera.Saturation = SaturationSlider.Value;
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
        CameraPreviewImage.Stretch = _settings.FaceZoomEnabled ? Stretch.UniformToFill : Stretch.Uniform;
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
            new HistoryWindow(_repository) { Owner = this }.Show();
        }
        ScannerInput.Focus();
    }

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs e) => await OpenSettingsAsync(false);

    private async Task OpenSettingsAsync(bool showCameraTab)
    {
        var previousCamera = _settings.Camera;
        var dialog = new SettingsWindow(_settings, showCameraTab) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SavedSettings is null)
        {
            ScannerInput.Focus();
            return;
        }
        _settings = dialog.SavedSettings;
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        _storageOptions!.RecordingRoot = _settings.RecordingRoot;
        _coordinator?.UpdateScannerProfile(_settings.Scanner);
        _syncDispatcher = new SyncDispatcher(_repository!, [new ExcelConnector(CreateExcelOptions())], new SystemClock());
        ApplySettingsVisuals();
        if (_recordingBackend is not null)
        {
            var restartRequired = previousCamera.SourceKind != _settings.Camera.SourceKind ||
                                  previousCamera.CameraIndex != _settings.Camera.CameraIndex ||
                                  previousCamera.AutoSelectBestCamera != _settings.Camera.AutoSelectBestCamera ||
                                  previousCamera.NetworkStreamUrl != _settings.Camera.NetworkStreamUrl ||
                                  previousCamera.NetworkUsername != _settings.Camera.NetworkUsername ||
                                  previousCamera.NetworkPasswordProtected != _settings.Camera.NetworkPasswordProtected ||
                                  previousCamera.HikvisionHost != _settings.Camera.HikvisionHost ||
                                  previousCamera.HikvisionRtspPort != _settings.Camera.HikvisionRtspPort ||
                                  previousCamera.HikvisionChannel != _settings.Camera.HikvisionChannel ||
                                  previousCamera.HikvisionSubStream != _settings.Camera.HikvisionSubStream ||
                                  previousCamera.Width != _settings.Camera.Width ||
                                  previousCamera.Height != _settings.Camera.Height ||
                                  Math.Abs(previousCamera.FramesPerSecond - _settings.Camera.FramesPerSecond) > 0.01;
            try
            {
                if (restartRequired)
                {
                    await _recordingBackend.RestartPreviewAsync(_settings.Camera, _lifetime.Token);
                }
                else
                {
                    await _recordingBackend.ApplyImageControlsAsync(
                        _settings.Camera.Brightness,
                        _settings.Camera.Contrast,
                        _settings.Camera.Sharpness,
                        _settings.Camera.Saturation,
                        _lifetime.Token);
                    await _recordingBackend.SetAutoFocusAsync(_settings.Camera.AutoFocus, _lifetime.Token);
                }
            }
            catch (Exception ex)
            {
                ShowCameraError(ex.Message);
            }
        }
        ScannerInput.Focus();
    }

    private void PopulateCameraSourceSelector()
    {
        _updatingCameraSourceSelector = true;
        CameraSourceSelector.Items.Clear();
        CameraSourceSelector.Items.Add(new CameraSourceChoice("自动选择本地摄像头", CameraSourceKind.AutoLocal));
        foreach (var camera in WindowsCameraDiscovery.Enumerate())
        {
            CameraSourceSelector.Items.Add(new CameraSourceChoice(camera.DisplayName, CameraSourceKind.WindowsCamera, camera.Index));
        }
        CameraSourceSelector.Items.Add(new CameraSourceChoice("IPC / 网络视频流", CameraSourceKind.NetworkStream));
        CameraSourceSelector.Items.Add(new CameraSourceChoice("海康 NVR / DVR", CameraSourceKind.HikvisionRecorder));
        CameraSourceSelector.DisplayMemberPath = nameof(CameraSourceChoice.Label);
        SelectCurrentCameraSourceChoice();
        _updatingCameraSourceSelector = false;
    }

    private void SelectCurrentCameraSourceChoice()
    {
        _updatingCameraSourceSelector = true;
        var choice = CameraSourceSelector.Items.Cast<CameraSourceChoice>().FirstOrDefault(item =>
            item.Kind == _settings.Camera.SourceKind &&
            (item.Kind != CameraSourceKind.WindowsCamera || item.CameraIndex == _settings.Camera.CameraIndex));
        CameraSourceSelector.SelectedItem = choice ?? CameraSourceSelector.Items[0];
        _updatingCameraSourceSelector = false;
    }

    private async void CameraSourceSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCameraSourceSelector || !IsLoaded ||
            CameraSourceSelector.SelectedItem is not CameraSourceChoice choice || _recordingBackend is null)
        {
            return;
        }
        if (_coordinator?.State is RecordingState.Recording or RecordingState.Starting or RecordingState.Saving)
        {
            FooterText.Text = "录像过程中不能切换摄像头";
            SelectCurrentCameraSourceChoice();
            return;
        }
        if (choice.Kind == CameraSourceKind.NetworkStream && string.IsNullOrWhiteSpace(_settings.Camera.NetworkStreamUrl) ||
            choice.Kind == CameraSourceKind.HikvisionRecorder && string.IsNullOrWhiteSpace(_settings.Camera.HikvisionHost))
        {
            SelectCurrentCameraSourceChoice();
            await OpenSettingsAsync(true);
            return;
        }

        _settings.Camera.SourceKind = choice.Kind;
        _settings.Camera.AutoSelectBestCamera = choice.Kind == CameraSourceKind.AutoLocal;
        if (choice.CameraIndex is not null)
        {
            _settings.Camera.CameraIndex = choice.CameraIndex.Value;
        }
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        CameraPreviewImage.Source = null;
        CameraPlaceholder.Visibility = Visibility.Visible;
        CameraStatusText.Text = "正在切换视频源…";
        try
        {
            await _recordingBackend.RestartPreviewAsync(_settings.Camera, _lifetime.Token);
            UpdateCameraRuntimeInfo();
            FooterText.Text = $"已切换到 {choice.Label}";
        }
        catch (Exception ex)
        {
            ShowCameraError(ex.Message);
        }
        ScannerInput.Focus();
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

    private void PlayRecording(RecentRecordingItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Record.VideoPath) || !File.Exists(item.Record.VideoPath))
        {
            FooterText.Text = "录像文件不存在";
            return;
        }
        new VideoPlayerWindow(item.Record.VideoPath) { Owner = this }.Show();
    }

    private void ApplySettingsVisuals()
    {
        OutputPathText.Text = GetCurrentOutputPath();
        AutoFocusButton.Content = _settings.Camera.AutoFocus ? "自动聚焦：开" : "自动聚焦：关";
        ApplyVoiceVisual();
        FaceZoomButton.Content = _settings.FaceZoomEnabled ? "面单放大：开" : "面单放大：关";
        CameraPreviewImage.Stretch = _settings.FaceZoomEnabled ? Stretch.UniformToFill : Stretch.Uniform;
        BrightnessSlider.Value = _settings.Camera.Brightness;
        ContrastSlider.Value = _settings.Camera.Contrast;
        SharpnessSlider.Value = _settings.Camera.Sharpness;
        SaturationSlider.Value = _settings.Camera.Saturation;
        ResolutionText.Text = $"{_settings.Camera.Width} × {_settings.Camera.Height}";
        FpsText.Text = $"{_settings.Camera.FramesPerSecond:0.#} fps";
        if (CameraSourceSelector.Items.Count > 0)
        {
            SelectCurrentCameraSourceChoice();
        }
        ApplyWorkflowVisuals();
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

    private ExcelConnectorOptions CreateExcelOptions() => new()
    {
        WorkbookPath = _settings.ExcelWorkbookPath
    };

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
        _lifetime.Cancel();
        _rawScanner?.Dispose();
        if (_recordingBackend is not null)
        {
            await _recordingBackend.DisposeAsync();
        }
        _speech.Dispose();
        _lifetime.Dispose();
        _allowClose = true;
        Close();
    }

    private sealed record CameraSourceChoice(string Label, CameraSourceKind Kind, int? CameraIndex = null);
}
