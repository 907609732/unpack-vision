using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

/// <summary>
/// Edits one detached camera profile. Nothing is written back to the settings window until the
/// user presses Save, so cancelling the dialog cannot partially mutate an active camera rig.
/// </summary>
public partial class CameraProfileDialog : Window
{
    private readonly Dictionary<int, WindowsCameraDevice> _cameraDevices;
    private readonly IHikvisionChannelDiscoveryService _hikvisionDiscovery;
    private readonly IIpcDiscoveryService _ipcDiscovery;
    private readonly ICameraConfigurationPreviewHost _cameraPreviewHost;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _previewTransitionTasks = [];
    private readonly object _previewTransitionSync = new();
    private IReadOnlyList<HikvisionChannelDescriptor> _discoveredChannels = [];
    private CancellationTokenSource? _previewCancellation;
    private Task? _previewTask;
    private Task? _ipcDiscoveryTask;
    private Task? _hikvisionDiscoveryTask;
    private Task? _shutdownTask;
    private bool _shutdownCompleted;
    private bool _closeRequested;
    private bool _closeAllowed;
    private bool _deferredCloseQueued;
    private bool _loading;
    private bool _discovering;
    private bool _discoveringIpc;

    internal CameraProfileDialog(
        CameraProfile source,
        IEnumerable<WindowsCameraDevice> cameraDevices,
        IHikvisionChannelDiscoveryService hikvisionDiscovery,
        IIpcDiscoveryService ipcDiscovery,
        ICameraConfigurationPreviewHost cameraPreviewHost)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cameraDevices);
        ArgumentNullException.ThrowIfNull(hikvisionDiscovery);
        ArgumentNullException.ThrowIfNull(ipcDiscovery);
        ArgumentNullException.ThrowIfNull(cameraPreviewHost);

        _cameraDevices = cameraDevices.ToDictionary(camera => camera.Index);
        _hikvisionDiscovery = hikvisionDiscovery;
        _ipcDiscovery = ipcDiscovery;
        _cameraPreviewHost = cameraPreviewHost;
        EditedProfile = Clone(source);
        InitializeComponent();
        Closing += CameraProfileDialog_OnClosing;

        foreach (var camera in _cameraDevices.Values.OrderBy(camera => camera.Index))
        {
            CameraIndexInput.Items.Add(new ComboBoxItem
            {
                Content = camera.DisplayName,
                Tag = camera.Index
            });
        }
        LoadProfile();
    }

    public CameraProfile EditedProfile { get; }

    public IReadOnlyList<HikvisionChannelDescriptor> DiscoveredChannels => _discoveredChannels;

    public bool AddAllDiscoveredChannels { get; private set; }

    private async void CameraProfileDialog_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed || _shutdownCompleted)
        {
            return;
        }

        if (_closeRequested)
        {
            e.Cancel = true;
            return;
        }

        if (!HasActiveBackgroundOperation())
        {
            // With no outstanding operation shutdown completes synchronously, so WPF can finish
            // this original close request without a nested Close call from inside Closing.
            ShutdownAsync().GetAwaiter().GetResult();
            _shutdownCompleted = true;
            _closeAllowed = true;
            return;
        }

        // WPF cannot synchronously await Closing. Keep the window alive but disabled until
        // discovery and preview operations have observed cancellation and released resources.
        e.Cancel = true;
        _closeRequested = true;
        IsEnabled = false;
        await ShutdownAsync();
        _shutdownCompleted = true;
        QueueDeferredClose();
    }

    private bool HasActiveBackgroundOperation() =>
        _ipcDiscoveryTask is { IsCompleted: false } ||
        _hikvisionDiscoveryTask is { IsCompleted: false } ||
        _previewTask is { IsCompleted: false } ||
        HasActivePreviewTransition() ||
        _shutdownTask is { IsCompleted: false };

    private bool HasActivePreviewTransition()
    {
        lock (_previewTransitionSync)
        {
            return _previewTransitionTasks.Any(task => !task.IsCompleted);
        }
    }

    private void QueueDeferredClose()
    {
        if (_deferredCloseQueued)
        {
            return;
        }

        _deferredCloseQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _deferredCloseQueued = false;
            if (!_closeRequested || !IsLoaded || !IsVisible || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _closeAllowed = true;
            Close();
        });
    }

    private void LoadProfile()
    {
        _loading = true;
        try
        {
            CameraNameInput.Text = EditedProfile.DisplayName;
            CameraSourceKindInput.SelectedIndex = (int)EditedProfile.SourceType;
            CameraIndexInput.SelectedItem = CameraIndexInput.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is int index &&
                    (!string.IsNullOrWhiteSpace(EditedProfile.WindowsSymbolicLink)
                        ? string.Equals(_cameraDevices.GetValueOrDefault(index)?.SymbolicLink,
                            EditedProfile.WindowsSymbolicLink, StringComparison.OrdinalIgnoreCase)
                        : index == EditedProfile.LegacyCameraIndex));
            if (CameraIndexInput.SelectedIndex < 0 && CameraIndexInput.Items.Count > 0)
            {
                CameraIndexInput.SelectedIndex = 0;
            }
            AutoBestCameraCheck.IsChecked = EditedProfile.AutoSelectBestCamera;
            NetworkStreamUrlInput.Text = EditedProfile.NetworkStreamUrl;
            NetworkUsernameInput.Text = EditedProfile.NetworkUsername;
            NetworkPasswordInput.Password = CameraCredentialProtector.Unprotect(EditedProfile.NetworkPasswordProtected);
            HikvisionHostInput.Text = EditedProfile.HikvisionHost;
            HikvisionHttpPortInput.Text = EditedProfile.HikvisionHttpPort.ToString();
            HikvisionPortInput.Text = EditedProfile.HikvisionRtspPort.ToString();
            HikvisionChannelInput.Text = EditedProfile.HikvisionChannel.ToString();
            HikvisionStreamInput.SelectedIndex = EditedProfile.HikvisionSubStream ? 1 : 0;
            HikvisionUsernameInput.Text = EditedProfile.NetworkUsername;
            HikvisionPasswordInput.Password = CameraCredentialProtector.Unprotect(EditedProfile.NetworkPasswordProtected);
            WidthInput.Text = EditedProfile.Width.ToString();
            HeightInput.Text = EditedProfile.Height.ToString();
            FpsInput.Text = EditedProfile.FramesPerSecond.ToString("0.##");
            AutoFocusCheck.IsChecked = EditedProfile.AutoFocus;
            BrightnessSlider.Value = EditedProfile.Brightness;
            ContrastSlider.Value = EditedProfile.Contrast;
            SharpnessSlider.Value = EditedProfile.Sharpness;
            SaturationSlider.Value = EditedProfile.Saturation;
        }
        finally
        {
            _loading = false;
        }
        UpdateSourceCards();
    }

    private async void CameraSourceKindInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateSourceCards();
            await RunTrackedPreviewTransitionAsync(RestartCameraPreviewAsync);
        }
    }

    private async void CameraProfileDialog_OnLoaded(object sender, RoutedEventArgs e) =>
        await RunTrackedPreviewTransitionAsync(RestartCameraPreviewAsync);

    private async void CameraIndexInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded && GetSelectedSourceKind() is CameraSourceKind.AutoLocal or CameraSourceKind.WindowsCamera)
        {
            await RunTrackedPreviewTransitionAsync(RestartCameraPreviewAsync);
        }
    }

    private void UpdateSourceCards()
    {
        if (!IsInitialized)
        {
            return;
        }

        var kind = GetSelectedSourceKind();
        LocalCameraCard.Visibility = kind is CameraSourceKind.AutoLocal or CameraSourceKind.WindowsCamera
            ? Visibility.Visible
            : Visibility.Collapsed;
        NetworkStreamCard.Visibility = kind == CameraSourceKind.NetworkStream
            ? Visibility.Visible
            : Visibility.Collapsed;
        HikvisionRecorderCard.Visibility = kind == CameraSourceKind.HikvisionRecorder
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoBestCameraCheck.Visibility = kind == CameraSourceKind.AutoLocal
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoFocusCheck.Visibility = kind is CameraSourceKind.AutoLocal or CameraSourceKind.WindowsCamera
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void DiscoverIpc_OnClick(object sender, RoutedEventArgs e)
    {
        if (_discoveringIpc)
        {
            return;
        }

        var operation = DiscoverIpcAsync();
        _ipcDiscoveryTask = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_ipcDiscoveryTask, operation))
            {
                _ipcDiscoveryTask = null;
            }
        }
    }

    private async Task DiscoverIpcAsync()
    {
        _discoveringIpc = true;
        DiscoverIpcButton.IsEnabled = false;
        IpcDiscoveryProgress.Visibility = Visibility.Visible;
        IpcDiscoveryProgressText.Visibility = Visibility.Visible;
        IpcDiscoveryResultList.Visibility = Visibility.Collapsed;
        IpcDiscoveryResultList.ItemsSource = null;
        IpcDiscoveryNoticeList.Visibility = Visibility.Collapsed;
        IpcDiscoveryNoticeList.ItemsSource = null;
        IpcDiscoverySourceText.Text = "正在扫描：ONVIF 标准 / 兼容探测 / 海康 SADP（可用时）";
        IpcDiscoveryStatusText.Text = "正在查询当前可用网卡，约需 5–10 秒…";
        try
        {
            var scanResult = await _ipcDiscovery.DiscoverAsync(
                new IpcDiscoveryRequest(TimeSpan.FromSeconds(5)),
                _lifetime.Token);
            var items = scanResult.Devices.Select(device => new IpcDiscoveryListItem(device)).ToArray();
            var notices = IpcDiscoveryPresentation.PrepareNotices(scanResult.Notices, scanResult.Devices);
            IpcDiscoveryResultList.ItemsSource = items;
            IpcDiscoveryResultList.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            IpcDiscoveryNoticeList.ItemsSource = notices;
            IpcDiscoveryNoticeList.Visibility = notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            IpcDiscoverySourceText.Text = $"本次返回来源：{IpcDiscoveryPresentation.GetSourceSummary(scanResult.Devices)}";
            IpcDiscoveryStatusText.Text = items.Length == 0
                ? "没有发现可用 IPC。请确认设备和电脑处于同一局域网；仍可手动填写 RTSP。"
                : $"已发现 {items.Length} 台设备。请选择设备查看候选连接方式。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            var notices = IpcDiscoveryPresentation.PrepareNotices([exception.Message]);
            IpcDiscoveryNoticeList.ItemsSource = notices;
            IpcDiscoveryNoticeList.Visibility = notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            IpcDiscoverySourceText.Text = "本次扫描未完成";
            IpcDiscoveryStatusText.Text = "扫描未能完成，请查看提示；仍可手动填写 RTSP。";
        }
        finally
        {
            _discoveringIpc = false;
            DiscoverIpcButton.IsEnabled = true;
            IpcDiscoveryProgress.Visibility = Visibility.Collapsed;
            IpcDiscoveryProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private async void IpcDiscoveryResultList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IpcDiscoveryResultList.SelectedItem is not IpcDiscoveryListItem item)
        {
            return;
        }

        var device = item.Device;
        ClearDiscoveredCandidateCredentials();
        await RunTrackedPreviewTransitionAsync(() => StopCameraPreviewAsync(updateStatus: false));
        if (!CanStartPreviewTransition())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CameraNameInput.Text) || CameraNameInput.Text.TrimStart().StartsWith("机位", StringComparison.Ordinal))
        {
            CameraNameInput.Text = item.SuggestedName;
        }
        if (!IpcDiscoveryPresentation.TryGetHikvisionRtspHost(device, out var rtspHost))
        {
            IpcDiscoveryStatusText.Text =
                $"已选择 {item.Title}（{item.KindText}）。发现服务地址不是视频流地址，软件不会把 ONVIF device_service 当作 RTSP；请从设备后台复制 RTSP 后再预览。";
            return;
        }

        NetworkStreamUrlInput.Text = CameraSourceUrlBuilder.BuildHikvisionRtspUrl(
            rtspHost,
            554,
            1,
            subStream: true);
        IpcDiscoveryStatusText.Text =
            $"已根据 {item.KindText} 结果填入海康 IPC 候选子码流。为防止把旧设备凭据发送给未验证设备，账号和密码已清空；请重新填写后点击“开始预览”。";
    }

    private void ClearDiscoveredCandidateCredentials()
    {
        NetworkUsernameInput.Clear();
        NetworkPasswordInput.Clear();
        HikvisionUsernameInput.Clear();
        HikvisionPasswordInput.Clear();
    }

    private async void DiscoverHikvisionChannels_OnClick(object sender, RoutedEventArgs e)
    {
        if (_discovering)
        {
            return;
        }

        var operation = DiscoverHikvisionChannelsAsync();
        _hikvisionDiscoveryTask = operation;
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_hikvisionDiscoveryTask, operation))
            {
                _hikvisionDiscoveryTask = null;
            }
        }
    }

    private async Task DiscoverHikvisionChannelsAsync()
    {
        try
        {
            ApplyInputs();
            if (EditedProfile.SourceType != CameraSourceType.HikvisionRecorder)
            {
                throw new ArgumentException("请先选择海康威视 NVR / DVR 来源");
            }

            _discovering = true;
            DiscoverHikvisionChannelsButton.IsEnabled = false;
            HikvisionDiscoveryProgress.Visibility = Visibility.Visible;
            HikvisionDiscoveryStatusText.Text = "正在登录录像机并读取通道，请稍候…";
            var request = new HikvisionDiscoveryRequest(
                EditedProfile.HikvisionHost,
                EditedProfile.HikvisionHttpPort,
                EditedProfile.NetworkUsername,
                CameraCredentialProtector.Unprotect(EditedProfile.NetworkPasswordProtected));
            _discoveredChannels = await _hikvisionDiscovery.DiscoverAsync(request, _lifetime.Token);
            var available = _discoveredChannels.Where(channel => channel.HasAnyStream).OrderBy(channel => channel.Channel).ToArray();
            if (available.Length == 0)
            {
                HikvisionDiscoveryStatusText.Text = "录像机没有返回可用通道，请先在录像机中接入并启用 IPC。";
                return;
            }

            HikvisionDiscoveredChannelInput.Items.Clear();
            foreach (var channel in available)
            {
                HikvisionDiscoveredChannelInput.Items.Add(new ComboBoxItem
                {
                    Content = FormatChannel(channel),
                    Tag = channel.Channel
                });
            }
            HikvisionDiscoveredChannelInput.Visibility = Visibility.Visible;
            HikvisionDiscoveredChannelInput.SelectedItem = HikvisionDiscoveredChannelInput.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is int channel && channel == EditedProfile.HikvisionChannel)
                ?? HikvisionDiscoveredChannelInput.Items[0];
            AddAllDiscoveredChannels = true;
            HikvisionDiscoveryStatusText.Text = $"已发现 {available.Length} 路。当前下拉框可选择本机位通道，保存后会补充其他通道（总机位最多{CameraRigOptions.ProductMaximumCameraCount}个）。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or
                                           InvalidOperationException or HttpRequestException or TaskCanceledException or
                                           InvalidDataException)
        {
            AddAllDiscoveredChannels = false;
            HikvisionDiscoveryStatusText.Text = $"通道发现失败：{exception.Message}";
            MessageBox.Show(this, HikvisionDiscoveryStatusText.Text, "海康通道发现", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _discovering = false;
            DiscoverHikvisionChannelsButton.IsEnabled = true;
            HikvisionDiscoveryProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void HikvisionDiscoveredChannelInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HikvisionDiscoveredChannelInput.SelectedItem is not ComboBoxItem { Tag: int channelNumber })
        {
            return;
        }

        var channel = _discoveredChannels.FirstOrDefault(item => item.Channel == channelNumber);
        if (channel is null)
        {
            return;
        }

        HikvisionChannelInput.Text = channel.Channel.ToString();
        var preferSub = HikvisionPreferSubStreamCheck.IsChecked == true && channel.SubStream is not null;
        var stream = preferSub ? channel.SubStream : channel.MainStream ?? channel.SubStream;
        HikvisionStreamInput.SelectedIndex = preferSub ? 1 : 0;
        if (stream is not null)
        {
            if (stream.Width > 0) WidthInput.Text = stream.Width.ToString();
            if (stream.Height > 0) HeightInput.Text = stream.Height.ToString();
            if (stream.FramesPerSecond > 0) FpsInput.Text = stream.FramesPerSecond.ToString("0.##");
        }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopCameraPreviewAsync(updateStatus: false);
            ApplyInputs();
            ValidateSource();
            await ShutdownAsync();
            _shutdownCompleted = true;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "无法保存机位", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        await ShutdownAsync();
        _shutdownCompleted = true;
        DialogResult = false;
    }

    private Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _lifetime.Cancel();
        _previewCancellation?.Cancel();
        await AwaitCancellationAsync(_ipcDiscoveryTask);
        await AwaitCancellationAsync(_hikvisionDiscoveryTask);
        await AwaitPreviewTransitionsAsync();
        await StopCameraPreviewAsync(updateStatus: false);
        _lifetime.Dispose();
    }

    private async Task AwaitPreviewTransitionsAsync()
    {
        while (true)
        {
            Task[] operations;
            lock (_previewTransitionSync)
            {
                operations = _previewTransitionTasks
                    .Where(task => !task.IsCompleted)
                    .ToArray();
            }
            if (operations.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(operations);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task AwaitCancellationAsync(Task? operation)
    {
        if (operation is null)
        {
            return;
        }

        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void StartCameraPreview_OnClick(object sender, RoutedEventArgs e) =>
        await RunTrackedPreviewTransitionAsync(RestartCameraPreviewAsync);

    private async void StopCameraPreview_OnClick(object sender, RoutedEventArgs e) =>
        await RunTrackedPreviewTransitionAsync(() => StopCameraPreviewAsync(updateStatus: true));

    private async Task RunTrackedPreviewTransitionAsync(Func<Task> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (_lifetime.IsCancellationRequested || _shutdownCompleted || _closeRequested)
        {
            return;
        }

        var operation = transition();
        lock (_previewTransitionSync)
        {
            _previewTransitionTasks.Add(operation);
        }
        try
        {
            await operation;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_previewTransitionSync)
            {
                _previewTransitionTasks.Remove(operation);
            }
        }
    }

    private bool CanStartPreviewTransition() =>
        IsLoaded &&
        IsVisible &&
        !_closeRequested &&
        !_shutdownCompleted &&
        !_lifetime.IsCancellationRequested;

    private async Task RestartCameraPreviewAsync()
    {
        if (_loading || !CanStartPreviewTransition())
        {
            return;
        }

        await StopCameraPreviewAsync(updateStatus: false);
        if (!CanStartPreviewTransition())
        {
            return;
        }
        try
        {
            ApplyInputs();
            ValidateSource();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            CameraPreviewStatusText.Text = exception.Message;
            CameraPreviewPlaceholder.Text = "请先完成当前来源配置";
            CameraPreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        CameraPreviewStatusText.Text = "正在连接摄像头…";
        CameraPreviewPlaceholder.Text = "正在连接摄像头，请稍候";
        CameraPreviewPlaceholder.Visibility = Visibility.Visible;
        StartCameraPreviewButton.IsEnabled = false;
        StopCameraPreviewButton.IsEnabled = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation;
        _previewTask = RunPreviewSessionAsync(Clone(EditedProfile), cancellation);
    }

    private async Task RunPreviewSessionAsync(CameraProfile profile, CancellationTokenSource cancellation)
    {
        try
        {
            await _cameraPreviewHost.RunCameraConfigurationPreviewAsync(
                profile,
                frame => Dispatcher.BeginInvoke(() => RenderPreviewFrame(frame, cancellation.Token)),
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    CameraPreviewStatusText.Text = profile.SourceType is CameraSourceType.NetworkStream or CameraSourceType.HikvisionRecorder
                        ? "无法打开画面，请检查账号、密码、码流地址和设备在线状态"
                        : "无法打开画面，请检查摄像头连接或是否被其他程序占用";
                    CameraPreviewPlaceholder.Text = "预览连接失败";
                    CameraPreviewPlaceholder.Visibility = Visibility.Visible;
                });
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    StartCameraPreviewButton.IsEnabled = true;
                    StopCameraPreviewButton.IsEnabled = false;
                });
            }
        }
    }

    private void RenderPreviewFrame(CameraConfigurationPreviewFrame frame, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return;
        }
        CameraPreviewImage.Source = UiImage.FromBytes(frame.JpegBytes);
        CameraPreviewPlaceholder.Visibility = Visibility.Collapsed;
        CameraPreviewStatusText.Text = $"实时预览 · {frame.Width} × {frame.Height} · {frame.FramesPerSecond:0.#} fps";
    }

    private async Task StopCameraPreviewAsync(bool updateStatus)
    {
        var cancellation = _previewCancellation;
        var task = _previewTask;
        _previewCancellation = null;
        _previewTask = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
        }
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
        StartCameraPreviewButton.IsEnabled = true;
        StopCameraPreviewButton.IsEnabled = false;
        if (updateStatus)
        {
            CameraPreviewStatusText.Text = "预览已停止";
        }
    }

    private void ApplyInputs()
    {
        var kind = GetSelectedSourceKind();
        EditedProfile.DisplayName = string.IsNullOrWhiteSpace(CameraNameInput.Text) ? "机位" : CameraNameInput.Text.Trim();
        EditedProfile.SourceType = (CameraSourceType)kind;
        EditedProfile.LegacyCameraIndex = CameraIndexInput.SelectedItem is ComboBoxItem { Tag: int cameraIndex }
            ? cameraIndex
            : 0;
        EditedProfile.WindowsSymbolicLink = _cameraDevices.GetValueOrDefault(EditedProfile.LegacyCameraIndex)?.SymbolicLink
            ?? EditedProfile.WindowsSymbolicLink;
        EditedProfile.AutoSelectBestCamera = kind == CameraSourceKind.AutoLocal;
        EditedProfile.Width = ParseInt(WidthInput.Text, "相机宽度", 160, 8192);
        EditedProfile.Height = ParseInt(HeightInput.Text, "相机高度", 120, 8192);
        EditedProfile.FramesPerSecond = ParseDouble(FpsInput.Text, "帧率", 1, 120);
        EditedProfile.AutoFocus = AutoFocusCheck.IsChecked == true;
        EditedProfile.Brightness = BrightnessSlider.Value;
        EditedProfile.Contrast = ContrastSlider.Value;
        EditedProfile.Sharpness = SharpnessSlider.Value;
        EditedProfile.Saturation = SaturationSlider.Value;
        EditedProfile.NetworkStreamUrl = NetworkStreamUrlInput.Text.Trim();
        EditedProfile.HikvisionHost = HikvisionHostInput.Text.Trim();
        EditedProfile.HikvisionHttpPort = ParseInt(HikvisionHttpPortInput.Text, "海康管理端口", 1, 65535);
        EditedProfile.HikvisionRtspPort = ParseInt(HikvisionPortInput.Text, "海康 RTSP 端口", 1, 65535);
        EditedProfile.HikvisionChannel = ParseInt(HikvisionChannelInput.Text, "海康通道号", 1, 999);
        EditedProfile.HikvisionSubStream = HikvisionStreamInput.SelectedIndex == 1;

        var isHikvision = kind == CameraSourceKind.HikvisionRecorder;
        EditedProfile.NetworkUsername = (isHikvision ? HikvisionUsernameInput.Text : NetworkUsernameInput.Text).Trim();
        var password = isHikvision ? HikvisionPasswordInput.Password : NetworkPasswordInput.Password;
        EditedProfile.NetworkPasswordProtected = CameraCredentialProtector.Protect(password);
    }

    private void ValidateSource()
    {
        if (EditedProfile.SourceType == CameraSourceType.NetworkStream)
        {
            CameraSourceUrlBuilder.AddCredentials(EditedProfile.NetworkStreamUrl, EditedProfile.NetworkUsername, string.Empty);
        }
        else if (EditedProfile.SourceType == CameraSourceType.HikvisionRecorder)
        {
            CameraSourceUrlBuilder.BuildHikvisionRtspUrl(
                EditedProfile.HikvisionHost,
                EditedProfile.HikvisionRtspPort,
                EditedProfile.HikvisionChannel,
                EditedProfile.HikvisionSubStream);
        }
    }

    private CameraSourceKind GetSelectedSourceKind()
    {
        if (CameraSourceKindInput.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<CameraSourceKind>(tag, out var kind))
        {
            return kind;
        }
        return CameraSourceKind.AutoLocal;
    }

    private static string FormatChannel(HikvisionChannelDescriptor channel)
    {
        var main = channel.MainStream is null ? "无主码流" : $"主码流 {channel.MainStream.Width}×{channel.MainStream.Height}";
        var sub = channel.SubStream is null ? "无子码流" : $"子码流 {channel.SubStream.Width}×{channel.SubStream.Height}";
        return $"通道 {channel.Channel} · {main} · {sub}";
    }

    private static int ParseInt(string value, string name, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"{name}必须在 {minimum} 到 {maximum} 之间");

    private static double ParseDouble(string value, string name, double minimum, double maximum) =>
        double.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"{name}必须在 {minimum} 到 {maximum} 之间");

    private static CameraProfile Clone(CameraProfile camera) => new()
    {
        Id = camera.Id,
        DisplayName = camera.DisplayName,
        Enabled = camera.Enabled,
        IsPrimary = camera.IsPrimary,
        SortOrder = camera.SortOrder,
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

    private sealed class IpcDiscoveryListItem
    {
        internal IpcDiscoveryListItem(IpcDiscoveryDevice device)
        {
            Device = device;
            IsHikvision = IpcDiscoveryPresentation.IsHikvisionCandidate(device);
            Title = FirstNotEmpty(
                device.DisplayName,
                string.Join(' ', new[] { device.Manufacturer, device.Model }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                $"IPC {device.RemoteAddress}");
            Details = string.Join(" · ", new[]
            {
                $"IP {device.RemoteAddress}",
                device.Manufacturer,
                device.Model
            }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
            KindText = IpcDiscoveryPresentation.GetSourceSummary([device]);
            ValidationText = IsHikvision
                ? "候选 RTSP · 需要实时预览验证"
                : "仅发现设备 · 需要手动填写 RTSP 并预览";
            SuggestedName = IsHikvision ? $"海康 IPC（{device.RemoteAddress}）" : Title;
        }

        internal IpcDiscoveryDevice Device { get; }

        public bool IsHikvision { get; }

        public string Title { get; }

        public string Details { get; }

        public string KindText { get; }

        public string ValidationText { get; }

        public string SuggestedName { get; }

        private static string FirstNotEmpty(params string[] values) =>
            values.First(value => !string.IsNullOrWhiteSpace(value));
    }
}
