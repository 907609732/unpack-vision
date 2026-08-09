using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using UnpackVision.Infrastructure;
using UnpackVision.Infrastructure.Diagnostics;
using UnpackVision.Core;

namespace UnpackVision.App;

public partial class SettingsWindow : Window
{
    private readonly LocalSettings _source;
    private readonly LoudSpeechService _speechTest = new();
    private readonly ObservableCollection<IssueTagDefinition> _issueTags = [];
    private readonly ObservableCollection<CameraProfile> _cameraProfiles = [];
    private readonly Dictionary<int, WindowsCameraDevice> _cameraDevices = [];
    private readonly IHikvisionChannelDiscoveryService _hikvisionChannelDiscovery;
    private readonly CancellationTokenSource _cameraDiscoveryLifetime = new();
    private CameraProfile? _editingCamera;
    private bool _loadingCamera;
    private bool _isMigratingRecordingRoot;
    private bool _isDiscoveringHikvisionChannels;
    private bool _isTestingCamera;
    private CameraTestProgressWindow? _cameraTestProgressWindow;

    public SettingsWindow(
        LocalSettings settings,
        IHikvisionChannelDiscoveryService hikvisionChannelDiscovery,
        bool showCameraTab = false)
    {
        _loadingCamera = true;
        _hikvisionChannelDiscovery = hikvisionChannelDiscovery;
        InitializeComponent();
        Closed += SettingsWindow_OnClosed;
        Closing += SettingsWindow_OnClosing;
        _source = settings;
        RecordingRootInput.Text = settings.RecordingRoot;
        ExcelPathInput.Text = settings.ExcelWorkbookPath;
        WorkspaceStatusText.Text = settings.Setup.IsComplete
            ? $"工作区 {settings.Setup.WorkspaceId:D} · 便携索引已启用"
            : "尚未完成工作区配置";
        MaximumMinutesInput.Text = settings.MaximumRecordingMinutes.ToString();
        LivePreviewCheck.IsChecked = settings.ShowLivePreview;
        VoiceCheck.IsChecked = settings.VoiceEnabled;
        VoiceVolumeSlider.Value = Math.Clamp(settings.VoiceVolume, 20, 100);
        foreach (var camera in WindowsCameraDiscovery.Enumerate())
        {
            _cameraDevices[camera.Index] = camera;
            CameraIndexInput.Items.Add(new ComboBoxItem
            {
                Content = camera.DisplayName,
                Tag = camera.Index
            });
        }
        CameraIndexInput.SelectedItem = CameraIndexInput.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is int index && index == settings.Camera.CameraIndex);
        if (CameraIndexInput.SelectedIndex < 0 && CameraIndexInput.Items.Count > 0)
        {
            CameraIndexInput.SelectedIndex = 0;
        }
        CameraSourceKindInput.SelectedIndex = (int)settings.Camera.SourceKind;
        AutoBestCameraCheck.IsChecked = settings.Camera.AutoSelectBestCamera;
        NetworkStreamUrlInput.Text = settings.Camera.NetworkStreamUrl;
        NetworkUsernameInput.Text = settings.Camera.NetworkUsername;
        NetworkPasswordInput.Password = CameraCredentialProtector.Unprotect(settings.Camera.NetworkPasswordProtected);
        HikvisionHostInput.Text = settings.Camera.HikvisionHost;
        HikvisionHttpPortInput.Text = settings.Camera.HikvisionHttpPort.ToString();
        HikvisionPortInput.Text = settings.Camera.HikvisionRtspPort.ToString();
        HikvisionChannelInput.Text = settings.Camera.HikvisionChannel.ToString();
        HikvisionStreamInput.SelectedIndex = settings.Camera.HikvisionSubStream ? 1 : 0;
        WidthInput.Text = settings.Camera.Width.ToString();
        HeightInput.Text = settings.Camera.Height.ToString();
        FpsInput.Text = settings.Camera.FramesPerSecond.ToString("0.##");
        AutoFocusCheck.IsChecked = settings.Camera.AutoFocus;
        BrightnessSlider.Value = settings.Camera.Brightness;
        ContrastSlider.Value = settings.Camera.Contrast;
        SharpnessSlider.Value = settings.Camera.Sharpness;
        SaturationSlider.Value = settings.Camera.Saturation;
        foreach (var profile in settings.CameraRig.Cameras.OrderBy(camera => camera.SortOrder))
        {
            _cameraProfiles.Add(CloneCameraProfile(profile));
        }
        CameraRigList.ItemsSource = _cameraProfiles;
        CameraRigList.SelectedItem = _cameraProfiles.FirstOrDefault(camera => camera.IsPrimary) ?? _cameraProfiles.FirstOrDefault();
        _editingCamera = CameraRigList.SelectedItem as CameraProfile;
        if (_editingCamera is not null)
        {
            LoadCameraProfile(_editingCamera);
        }
        _loadingCamera = false;
        ApplyCameraRigMode(settings.CameraRig.Mode);
        UpdateRigEstimate();
        MinimumLengthInput.Text = settings.Scanner.MinimumLength.ToString();
        MaximumLengthInput.Text = settings.Scanner.MaximumLength.ToString();
        FilterPrefixCheck.IsChecked = settings.Scanner.FilterPrefixEnabled;
        PrefixInput.Text = settings.Scanner.PrefixToRemove;
        FilterSuffixCheck.IsChecked = settings.Scanner.FilterSuffixEnabled;
        SuffixInput.Text = settings.Scanner.SuffixToRemove;
        DebounceInput.Text = settings.Scanner.DebounceMilliseconds.ToString();
        CaptureIssueSnapshotCheck.IsChecked = settings.CaptureSnapshotOnIssueTag;
        AutoUpdateCheck.IsChecked = settings.AutoCheckUpdates;
        TelemetryCheck.IsChecked = settings.Telemetry.Enabled;
        AboutVersionText.Text = $"版本 {ProductInfo.Version}";
        RepositoryUrlText.Text = ProductInfo.RepositoryUrl;
        AndroidDownloadUrlText.Text = ProductInfo.AndroidDownloadUrl;
        AndroidDownloadQr.Source = ToBitmapImage(
            BarcodePresentationService.CreateQrCodePng(ProductInfo.AndroidDownloadUrl, 360));
        App.Updates.StatusChanged += Updates_OnStatusChanged;
        RenderUpdateStatus(App.Updates.Status);
        foreach (var tag in settings.IssueTags.OrderBy(item => item.SortOrder))
        {
            _issueTags.Add(tag with { });
        }
        IssueTagsGrid.ItemsSource = _issueTags;
        UpdateCameraSourceFields();
        if (showCameraTab)
        {
            SettingsTabControl.SelectedIndex = 1;
        }
    }

    public LocalSettings? SavedSettings { get; private set; }

    private void OpenDevicePairing_OnClick(object sender, RoutedEventArgs e)
    {
        using var operation = App.UiWatchdog?.BeginOperation("settings.open-pairing");
        DiagnosticLog.Information("用户打开手机配对窗口");
        new DevicePairingWindow { Owner = this }.ShowDialog();
        DiagnosticLog.Information("手机配对窗口已关闭");
    }

    private void OpenPairedDevices_OnClick(object sender, RoutedEventArgs e)
    {
        using var operation = App.UiWatchdog?.BeginOperation("settings.open-paired-devices");
        DiagnosticLog.Information("用户打开已配对设备窗口");
        new PairedDevicesWindow { Owner = this }.ShowDialog();
        DiagnosticLog.Information("已配对设备窗口已关闭");
    }

    private void OpenLogDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.LogRootDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(DiagnosticLog.LogRootDirectory);
            Process.Start(startInfo);
            DiagnosticLog.Information("用户打开诊断日志目录");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(exception, "打开诊断日志目录失败");
            MessageBox.Show(
                this,
                $"无法打开日志目录：{exception.Message}",
                "诊断日志",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BrowseRecordingRoot_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择录像保存目录", InitialDirectory = RecordingRootInput.Text };
        if (dialog.ShowDialog(this) == true)
        {
            RecordingRootInput.Text = dialog.FolderName;
        }
    }

    private void BrowseExcel_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", FileName = ExcelPathInput.Text };
        if (dialog.ShowDialog(this) == true)
        {
            ExcelPathInput.Text = dialog.FileName;
        }
    }

    private async void CreateExcel_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "生成标准退货扫码表格",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            FileName = $"退货扫码记录_{DateTime.Now:yyyy年MM月}.xlsx",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var service = new WorkbookTemplateService();
            await service.CreateAsync(dialog.FileName);
            ExcelPathInput.Text = dialog.FileName;
            MessageBox.Show(this, "标准六列表格已生成并绑定。", "表格已创建");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TestVoiceButton_OnClick(object sender, RoutedEventArgs e) =>
        _speechTest.Speak("语音播报音量测试", (int)Math.Round(VoiceVolumeSlider.Value));

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentCameraProfile();
            var enabledCameras = _cameraProfiles.Where(camera => camera.Enabled).OrderBy(camera => camera.SortOrder).ToArray();
            if (enabledCameras.Length is < 1 or > CameraRigOptions.MaximumEnabledCameras)
            {
                throw new ArgumentException("机位方案必须启用1到4个机位");
            }
            if (enabledCameras.Count(camera => camera.IsPrimary) != 1)
            {
                throw new ArgumentException("必须且只能设置一个主机位");
            }
            var activeCameras = GetActiveCameraProfiles();
            if (IsMultiCameraMode && activeCameras.Length > 1 && !_source.CameraRig.LastPerformanceTestPassed)
            {
                var testNow = MessageBox.Show(
                    this,
                    "启用多机位前需要进行一次15秒性能检测。检测会验证画面、帧率和磁盘写入，是否现在开始？",
                    "测试并启用多机位",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (testNow != MessageBoxResult.Yes || !await RunCameraRigPerformanceTestAsync())
                {
                    return;
                }
            }
            foreach (var camera in activeCameras)
            {
                ValidateCameraSource(ToCameraOptions(camera));
            }
            var primaryCamera = activeCameras.Single(camera => camera.IsPrimary);
            var minimum = ParseInt(MinimumLengthInput.Text, "最小单号长度", 1, 200);
            var maximum = ParseInt(MaximumLengthInput.Text, "最大单号长度", minimum, 200);
            var candidate = new LocalSettings
            {
                Workflow = _source.Workflow,
                MaximumRecordingMinutes = ParseInt(MaximumMinutesInput.Text, "最长录像分钟数", 1, 1440),
                RecordingRoot = Require(RecordingRootInput.Text, "录像保存位置"),
                ExcelWorkbookPath = ExcelPathInput.Text.Trim(),
                ShowLivePreview = LivePreviewCheck.IsChecked == true,
                VoiceEnabled = VoiceCheck.IsChecked == true,
                VoiceVolume = (int)Math.Round(VoiceVolumeSlider.Value),
                FaceZoomEnabled = _source.FaceZoomEnabled,
                CaptureSnapshotOnIssueTag = CaptureIssueSnapshotCheck.IsChecked == true,
                AutoCheckUpdates = AutoUpdateCheck.IsChecked == true,
                Consent = _source.Consent,
                Setup = _source.Setup,
                Telemetry = new TelemetryConsentState
                {
                    Enabled = TelemetryCheck.IsChecked == true,
                    ChangedAt = _source.Telemetry.Enabled == (TelemetryCheck.IsChecked == true)
                        ? _source.Telemetry.ChangedAt
                        : DateTimeOffset.Now,
                    WithdrawnAt = TelemetryCheck.IsChecked == true ? null :
                        _source.Telemetry.WithdrawnAt ?? DateTimeOffset.Now
                },
                Donation = _source.Donation,
                IssueTags = ValidateIssueTags(),
                Scanner = _source.Scanner with
                {
                    MinimumLength = minimum,
                    MaximumLength = maximum,
                    FilterPrefixEnabled = FilterPrefixCheck.IsChecked == true,
                    PrefixToRemove = PrefixInput.Text,
                    FilterSuffixEnabled = FilterSuffixCheck.IsChecked == true,
                    SuffixToRemove = SuffixInput.Text,
                    DebounceMilliseconds = ParseInt(DebounceInput.Text, "防误扫间隔", 0, 60_000)
                },
                Camera = ToCameraOptions(primaryCamera),
                CameraRig = new CameraRigOptions
                {
                    SchemaVersion = CameraRigOptions.CurrentSchemaVersion,
                    Name = _source.CameraRig.Name,
                    Mode = SelectedCameraRigMode,
                    // Keep inactive angles in the saved rig: single-camera mode is a runtime
                    // choice, not a destructive reset of a carefully configured multi-camera rig.
                    Cameras = _cameraProfiles.Select(CloneCameraProfile).ToList(),
                    CompositeWidth = 1920,
                    CompositeHeight = 1080,
                    CompositeFramesPerSecond = 15,
                    LastPerformanceTestAt = _source.CameraRig.LastPerformanceTestAt,
                    LastPerformanceTestPassed = _source.CameraRig.LastPerformanceTestPassed
                }
            };
            if (!await MigrateRecordingRootIfRequestedAsync(candidate.RecordingRoot))
            {
                return;
            }
            SavedSettings = candidate;
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "配置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "录像迁移失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, ex.Message, "录像迁移失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<bool> MigrateRecordingRootIfRequestedAsync(string targetRoot)
    {
        var sourceRoot = Path.GetFullPath(_source.RecordingRoot);
        var destinationRoot = Path.GetFullPath(targetRoot);
        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var migration = new RecordingRootMigrationService();
        var preview = await migration.PreviewAsync(sourceRoot, destinationRoot);
        if (!preview.HasContent)
        {
            return true;
        }

        var choice = MessageBox.Show(
            this,
            $"检测到旧录像目录中有 {preview.FileCount:N0} 个录像或迁移索引，共 {FormatBytes(preview.TotalBytes)}。\n\n" +
            "是否将它们打包迁移到新位置？\n\n" +
            "选择“是”会复制并逐个校验所有录像及 .unpackvision 迁移索引；旧文件会保留，不会自动删除。\n" +
            "选择“否”只修改以后新录像的保存位置。",
            "迁移旧录像到新位置",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel)
        {
            return false;
        }
        if (choice == MessageBoxResult.No)
        {
            return true;
        }

        var originalTitle = Title;
        ShowMigrationProgress(0, preview.FileCount, 0, preview.TotalBytes, "正在准备迁移清单…");
        _isMigratingRecordingRoot = true;
        SettingsTabControl.IsEnabled = false;
        SettingsActionBar.IsEnabled = false;
        MigrationProgressOverlay.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<RecordingRootMigrationProgress>(value =>
            {
                ShowMigrationProgress(
                    value.CompletedFiles,
                    value.TotalFiles,
                    value.CompletedBytes,
                    value.TotalBytes,
                    value.RelativePath);
            });
            var result = await migration.MigrateAsync(preview, progress);
            WorkspaceStatusText.Text =
                $"迁移完成：新复制 {result.CopiedFiles:N0} 个文件，复用已校验文件 {result.VerifiedExistingFiles:N0} 个。旧目录仍完整保留。";
            MessageBox.Show(
                this,
                $"迁移完成。\n\n新复制：{result.CopiedFiles:N0} 个文件\n" +
                $"复用已校验：{result.VerifiedExistingFiles:N0} 个文件\n" +
                "旧录像目录没有被删除。请确认新盘录像可播放后，再自行清理旧盘文件。",
                "旧录像已安全迁移",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return true;
        }
        finally
        {
            Title = originalTitle;
            _isMigratingRecordingRoot = false;
            MigrationProgressOverlay.Visibility = Visibility.Collapsed;
            SettingsTabControl.IsEnabled = true;
            SettingsActionBar.IsEnabled = true;
        }
    }

    private void ShowMigrationProgress(
        int completedFiles,
        int totalFiles,
        long completedBytes,
        long totalBytes,
        string currentFile)
    {
        var percentage = totalBytes <= 0
            ? (totalFiles == 0 ? 0 : completedFiles * 100d / totalFiles)
            : Math.Clamp(completedBytes * 100d / totalBytes, 0, 100);
        MigrationProgressBar.Value = percentage;
        MigrationProgressPercentText.Text = $"{percentage:0}%";
        MigrationProgressCountText.Text = $"{completedFiles:N0}/{totalFiles:N0} 个文件 · {FormatBytes(completedBytes)}/{FormatBytes(totalBytes)}";
        MigrationProgressDetailText.Text = $"当前文件：{currentFile}";
        Title = $"迁移录像 {percentage:0}% · 拆包智录";
        WorkspaceStatusText.Text = $"正在迁移：{currentFile}（{completedFiles:N0}/{totalFiles:N0}）";
    }

    private void SettingsWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isTestingCamera)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "正在测试摄像头。为保证帧率和磁盘测速结果准确，请等待测试完成后再关闭设置窗口。",
                "摄像头测试进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!_isMigratingRecordingRoot)
        {
            return;
        }

        e.Cancel = true;
        MessageBox.Show(
            this,
            "正在复制并校验录像。为保证迁移结果完整，请等待进度完成后再关闭设置窗口。",
            "录像迁移进行中",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:F2} GB"
            : $"{bytes / 1024d / 1024d:F1} MB";

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void CheckUpdate_OnClick(object sender, RoutedEventArgs e) =>
        await App.Updates.CheckAndDownloadAsync(force: true);

    private async void InstallUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mainWindow)
        {
            await mainWindow.TryApplyUpdateAsync();
        }
    }

    private void OpenProductLink_OnClick(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkElement)?.Tag?.ToString() switch
        {
            "repository" => ProductInfo.RepositoryUrl,
            "windows" => ProductInfo.WindowsDownloadUrl,
            "android" => ProductInfo.AndroidDownloadUrl,
            "security" => LegalDocuments.SecurityUrl,
            _ => ProductInfo.LatestReleaseUrl
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenTerms_OnClick(object sender, RoutedEventArgs e) =>
        new LegalDocumentWindow("用户协议", LegalDocuments.TermsText) { Owner = this }.ShowDialog();

    private void OpenPrivacy_OnClick(object sender, RoutedEventArgs e) =>
        new LegalDocumentWindow("隐私政策", LegalDocuments.PrivacyText) { Owner = this }.ShowDialog();

    private void OpenDonation_OnClick(object sender, RoutedEventArgs e) =>
        new DonationWindow(_source.Donation) { Owner = this }.ShowDialog();

    private async void CheckRecovery_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = await CreateRecoveryService().PreviewAsync(
                RecordingRootInput.Text.Trim(),
                ExcelPathInput.Text.Trim());
            MessageBox.Show(
                this,
                $"完整 {preview.CompleteCount}\n" +
                $"仅文件名 {preview.FileNameOnlyCount}\n" +
                $"Excel 关联 {preview.ExcelMatchedCount}\n" +
                $"冲突 {preview.ConflictCount}\n" +
                $"录像缺失 {preview.MissingVideoCount}\n" +
                $"无效 {preview.InvalidCount}",
                "旧数据检查完成",
                MessageBoxButton.OK,
                preview.InvalidCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "检查失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RecoverWorkspace_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "恢复前会备份当前数据库，并以字段更新时间智能合并。不会删除或覆盖录像文件。是否继续？",
                "重建本机数据库",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            var service = CreateRecoveryService();
            var preview = await service.PreviewAsync(
                RecordingRootInput.Text.Trim(),
                ExcelPathInput.Text.Trim());
            var result = await service.RecoverAsync(preview);
            MessageBox.Show(
                this,
                $"恢复完成：新增 {result.Added}，更新 {result.Updated}，跳过 {result.Skipped}。\n\n报告：{result.ReportPath}",
                "恢复完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenRecoveryReports_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = Path.Combine(
                Path.GetFullPath(RecordingRootInput.Text.Trim()),
                ".unpackvision",
                "recovery-reports");
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开恢复报告");
        }
    }

    private WorkspaceRecoveryService CreateRecoveryService()
    {
        var storage = new StorageOptions { RecordingRoot = Require(RecordingRootInput.Text, "录像保存位置") };
        var repository = new SqliteScanRecordRepository(storage);
        repository.InitializeAsync().GetAwaiter().GetResult();
        return new WorkspaceRecoveryService(repository, storage);
    }

    private void ConfigureFirewall_OnClick(object sender, RoutedEventArgs e)
    {
        var script = Path.Combine(
            AppContext.BaseDirectory,
            "Scripts",
            "configure-private-firewall.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show(this, "当前便携调试目录不包含防火墙配置脚本，请使用完整安装版。", "防火墙配置");
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit();
            if (process?.ExitCode == 0)
            {
                MessageBox.Show(
                    this,
                    "已仅为 Windows“专用网络”放行手机协同端口；公共网络仍然禁止。",
                    "防火墙配置完成");
            }
            else
            {
                MessageBox.Show(this, "防火墙配置未完成，请确认管理员授权。", "防火墙配置");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "已取消管理员授权，防火墙没有改动。", "防火墙配置");
        }
    }

    private void Updates_OnStatusChanged(object? sender, DesktopUpdateStatus status) =>
        Dispatcher.Invoke(() => RenderUpdateStatus(status));

    private void RenderUpdateStatus(DesktopUpdateStatus status)
    {
        UpdateStatusText.Text = status.Message;
        UpdateProgress.Visibility = status.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = status.Progress ?? 0;
        InstallUpdateButton.IsEnabled = status.ReadyToInstall;
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        App.Updates.StatusChanged -= Updates_OnStatusChanged;
        _cameraDiscoveryLifetime.Cancel();
        _cameraDiscoveryLifetime.Dispose();
        _speechTest.Dispose();
    }

    private async void SafeUninstall_OnClick(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow mainWindow && mainWindow.IsRecordingOperationActive)
        {
            MessageBox.Show(this, "正在录像、启动或保存时不能卸载。请先完成当前包裹。", "暂不能卸载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var updater = InstalledUninstallLauncher.FindUpdater();
        if (updater is null)
        {
            MessageBox.Show(this,
                "当前运行的是开发版或便携版，没有检测到 Velopack 卸载器。\n\n正式安装版可在 Windows 设置 → 应用 → 已安装的应用 → 拆包智录 → 卸载。",
                "未检测到正式安装",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new SafeUninstallWindow(_source) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (Owner is MainWindow owner)
            {
                await owner.PrepareForUninstallAsync();
            }
            await UninstallCleanupService.PrepareAsync(_source, dialog.DeleteBusinessData);
            StartupRegistration.RemoveCurrentUserStartup();
            InstalledUninstallLauncher.Start(updater);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            UninstallCleanupService.DiscardPendingPlan();
            MessageBox.Show(this, $"无法启动安全卸载：{exception.Message}", "卸载失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static BitmapImage ToBitmapImage(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void AddIssueTag_OnClick(object sender, RoutedEventArgs e)
    {
        var id = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var tag = new IssueTagDefinition
        {
            Id = id,
            Name = $"新标签{_issueTags.Count + 1}",
            ColorHex = "#FF9500",
            BarcodeValue = $"UV-TAG-{id}",
            SortOrder = _issueTags.Count
        };
        _issueTags.Add(tag);
        IssueTagsGrid.SelectedItem = tag;
        IssueTagsGrid.ScrollIntoView(tag);
    }

    private void DeleteIssueTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (IssueTagsGrid.SelectedItem is IssueTagDefinition tag)
        {
            _issueTags.Remove(tag);
        }
    }

    private List<IssueTagDefinition> ValidateIssueTags()
    {
        IssueTagsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        IssueTagsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var tags = _issueTags.Select((tag, index) => tag with
        {
            Name = tag.Name.Trim(),
            BarcodeValue = tag.BarcodeValue.Trim(),
            ColorHex = tag.ColorHex.Trim(),
            SortOrder = index
        }).ToList();
        if (tags.Any(tag => string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.BarcodeValue)))
        {
            throw new ArgumentException("异常标签名称和条码内容不能为空");
        }
        if (tags.GroupBy(tag => tag.BarcodeValue, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            tags.Any(tag => string.Equals(tag.BarcodeValue, IssueTagDefaults.UndoBarcode, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("异常标签条码必须唯一，且不能与撤销条码相同");
        }
        if (tags.Any(tag => !System.Text.RegularExpressions.Regex.IsMatch(tag.ColorHex, "^#[0-9A-Fa-f]{6}$")))
        {
            throw new ArgumentException("标签颜色必须使用 #RRGGBB 格式，例如 #FF3B30");
        }
        return tags;
    }

    private void CameraSourceKindInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCameraSourceFields();

    private bool IsMultiCameraMode => MultiCameraModeOption.IsChecked == true;

    private CameraRigMode SelectedCameraRigMode => IsMultiCameraMode
        ? CameraRigMode.MultiCamera
        : CameraRigMode.SingleCamera;

    private CameraProfile[] GetActiveCameraProfiles()
    {
        var enabled = _cameraProfiles.Where(camera => camera.Enabled)
            .OrderBy(camera => camera.SortOrder)
            .ToArray();
        if (SelectedCameraRigMode == CameraRigMode.MultiCamera)
        {
            return enabled;
        }

        var primary = enabled.FirstOrDefault(camera => camera.IsPrimary) ?? enabled.FirstOrDefault();
        return primary is null ? [] : [primary];
    }

    private void CameraRigMode_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_loadingCamera)
        {
            return;
        }

        SaveCurrentCameraProfile();
        ApplyCameraRigMode(SelectedCameraRigMode);
    }

    private void ApplyCameraRigMode(CameraRigMode mode)
    {
        _loadingCamera = true;
        try
        {
            SingleCameraModeOption.IsChecked = mode == CameraRigMode.SingleCamera;
            MultiCameraModeOption.IsChecked = mode == CameraRigMode.MultiCamera;
        }
        finally
        {
            _loadingCamera = false;
        }

        var isMulti = mode == CameraRigMode.MultiCamera;
        MultiCameraManagementPanel.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;
        TestAllCamerasButton.Visibility = isMulti ? Visibility.Visible : Visibility.Collapsed;
        CameraModeDescriptionText.Text = isMulti
            ? "多机位将同时预览、保存每路原片并生成合成录像。保存前需通过全部机位性能测试。"
            : "单机位仅使用主机位，保留最常用的来源与画面设置；已有其他机位不会被删除。";
        if (!isMulti)
        {
            var primary = _cameraProfiles.FirstOrDefault(camera => camera.Enabled && camera.IsPrimary)
                ?? _cameraProfiles.FirstOrDefault(camera => camera.Enabled);
            if (primary is not null && !ReferenceEquals(_editingCamera, primary))
            {
                CameraRigList.SelectedItem = primary;
                _editingCamera = primary;
                LoadCameraProfile(primary);
            }
        }
        UpdateRigEstimate();
    }

    private void CameraRigList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCamera) return;
        SaveCurrentCameraProfile();
        _editingCamera = CameraRigList.SelectedItem as CameraProfile;
        CameraMoreActionsButton.IsEnabled = _editingCamera is not null;
        if (_editingCamera is not null) LoadCameraProfile(_editingCamera);
    }

    private void CameraMoreActions_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button || CameraRigList.SelectedItem is null)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void AddCamera_OnClick(object sender, RoutedEventArgs e)
    {
        SaveCurrentCameraProfile();
        if (_cameraProfiles.Count >= CameraRigOptions.MaximumEnabledCameras)
        {
            MessageBox.Show(this, "最多只能启用4个机位", "机位管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var camera = new CameraProfile
        {
            DisplayName = $"机位 {_cameraProfiles.Count + 1}",
            SortOrder = _cameraProfiles.Count,
            SourceType = CameraSourceType.WindowsCamera,
            LegacyCameraIndex = _cameraDevices.Keys.FirstOrDefault(),
            AutoSelectBestCamera = false,
            Width = 1920,
            Height = 1080,
            FramesPerSecond = 15
        };
        _cameraProfiles.Add(camera);
        _source.CameraRig.LastPerformanceTestPassed = false;
        CameraRigList.SelectedItem = camera;
        UpdateRigEstimate();
    }

    private void RemoveCamera_OnClick(object sender, RoutedEventArgs e)
    {
        if (CameraRigList.SelectedItem is not CameraProfile camera) return;
        if (_cameraProfiles.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一个机位", "机位管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var index = _cameraProfiles.IndexOf(camera);
        _cameraProfiles.Remove(camera);
        if (camera.IsPrimary) _cameraProfiles[0].IsPrimary = true;
        NormalizeCameraOrder();
        _source.CameraRig.LastPerformanceTestPassed = false;
        CameraRigList.SelectedIndex = Math.Min(index, _cameraProfiles.Count - 1);
        CameraRigList.Items.Refresh();
        UpdateRigEstimate();
    }

    private void SetPrimaryCamera_OnClick(object sender, RoutedEventArgs e)
    {
        if (CameraRigList.SelectedItem is not CameraProfile selected) return;
        foreach (var camera in _cameraProfiles) camera.IsPrimary = ReferenceEquals(camera, selected);
        CameraRigList.Items.Refresh();
        CameraRigStatusText.Text = $"主机位：{selected.DisplayName}";
    }

    private void MoveCameraUp_OnClick(object sender, RoutedEventArgs e) => MoveSelectedCamera(-1);
    private void MoveCameraDown_OnClick(object sender, RoutedEventArgs e) => MoveSelectedCamera(1);

    private void MoveSelectedCamera(int delta)
    {
        if (CameraRigList.SelectedItem is not CameraProfile selected) return;
        var index = _cameraProfiles.IndexOf(selected);
        var target = index + delta;
        if (target < 0 || target >= _cameraProfiles.Count) return;
        _cameraProfiles.Move(index, target);
        NormalizeCameraOrder();
        CameraRigList.SelectedItem = selected;
        UpdateRigEstimate();
    }

    private async void TestCameraRig_OnClick(object sender, RoutedEventArgs e)
    {
        await RunCameraRigPerformanceTestAsync();
    }

    private async Task<bool> RunCameraRigPerformanceTestAsync()
    {
        try
        {
            SaveCurrentCameraProfile();
            var rig = new CameraRigOptions
            {
                Mode = CameraRigMode.MultiCamera,
                Cameras = _cameraProfiles.Select(CloneCameraProfile).ToList(),
                CompositeWidth = 1920,
                CompositeHeight = 1080,
                CompositeFramesPerSecond = 15
            };
            BeginCameraTest("正在准备全部机位测试");
            var progress = CreateCameraTestProgress();
            var result = Owner is MainWindow mainWindow
                ? await mainWindow.TestCandidateCameraRigAsync(rig, progress)
                : throw new InvalidOperationException("无法连接主窗口执行机位测试");
            _source.CameraRig.LastPerformanceTestAt = DateTimeOffset.Now;
            _source.CameraRig.LastPerformanceTestPassed = result.Passed;
            CameraRigStatusText.Text = $"{result.Summary} · 预计 {result.EstimatedMegabytesPerHour / 1024:F1} GB/小时 · 可录 {result.AvailableRecordingHours:F1} 小时";
            var detail = result.Passed
                ? CameraRigStatusText.Text
                : string.Join(Environment.NewLine, result.Cameras
                    .Where(item => !item.Passed)
                    .Select(item => $"{item.DisplayName}：{item.Message}"));
            CompleteCameraTest(
                result.Passed,
                result.Passed ? "全部机位测试通过" : "部分机位未通过",
                detail);
            return result.Passed;
        }
        catch (Exception ex)
        {
            _source.CameraRig.LastPerformanceTestPassed = false;
            CameraRigStatusText.Text = $"测试失败：{ex.Message}";
            CompleteCameraTest(false, "机位性能测试失败", ex.Message);
            return false;
        }
        finally
        {
            EndCameraTest();
        }
    }

    private async void TestCurrentCamera_OnClick(object sender, RoutedEventArgs e)
    {
        if (CameraRigList.SelectedItem is not CameraProfile selected) return;
        try
        {
            SaveCurrentCameraProfile();
            var candidate = CloneCameraProfile(selected);
            candidate.IsPrimary = true;
            candidate.SortOrder = 0;
            var rig = new CameraRigOptions
            {
                Mode = CameraRigMode.SingleCamera,
                Cameras = [candidate],
                CompositeWidth = 1920,
                CompositeHeight = 1080,
                CompositeFramesPerSecond = 15
            };
            BeginCameraTest($"正在准备测试“{candidate.DisplayName}”");
            var progress = CreateCameraTestProgress(candidate.DisplayName);
            var result = Owner is MainWindow mainWindow
                ? await mainWindow.TestCandidateCameraRigAsync(rig, progress)
                : throw new InvalidOperationException("无法连接主窗口执行机位测试");
            var camera = result.Cameras.FirstOrDefault();
            CameraRigStatusText.Text = camera is null
                ? result.Summary
                : $"{camera.DisplayName}：{camera.Message} · 实际 {camera.ActualFramesPerSecond:F1} fps · 丢帧 {camera.DroppedFrameRatio * 100:F1}%";
            CompleteCameraTest(
                result.Passed,
                result.Passed ? "当前机位测试通过" : "当前机位测试未通过",
                CameraRigStatusText.Text);
        }
        catch (Exception ex)
        {
            CameraRigStatusText.Text = $"当前机位测试失败：{ex.Message}";
            CompleteCameraTest(false, "当前机位测试失败", ex.Message);
        }
        finally
        {
            EndCameraTest();
        }
    }

    private void BeginCameraTest(string message)
    {
        if (_isTestingCamera)
        {
            throw new InvalidOperationException("机位测试正在进行中");
        }

        _isTestingCamera = true;
        _cameraTestProgressWindow?.CloseFinished();
        var progressWindow = new CameraTestProgressWindow(message) { Owner = this };
        _cameraTestProgressWindow = progressWindow;
        progressWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cameraTestProgressWindow, progressWindow))
            {
                _cameraTestProgressWindow = null;
            }
        };
        progressWindow.Show();
        TestCurrentCameraButton.IsEnabled = false;
        TestAllCamerasButton.IsEnabled = false;
        SettingsTabControl.IsHitTestVisible = false;
        SettingsActionBar.IsHitTestVisible = false;
    }

    private IProgress<CameraPerformanceProgress> CreateCameraTestProgress(string? cameraName = null) =>
        new Progress<CameraPerformanceProgress>(progress =>
        {
            _cameraTestProgressWindow?.Report(progress, cameraName);
        });

    private void EndCameraTest()
    {
        _isTestingCamera = false;
        TestCurrentCameraButton.IsEnabled = true;
        TestAllCamerasButton.IsEnabled = true;
        SettingsTabControl.IsHitTestVisible = true;
        SettingsActionBar.IsHitTestVisible = true;
    }

    private void CompleteCameraTest(bool success, string title, string detail)
    {
        if (_cameraTestProgressWindow is { } progressWindow)
        {
            progressWindow.Complete(success, title, detail);
            return;
        }

        MessageBox.Show(
            this,
            detail,
            title,
            MessageBoxButton.OK,
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void DiscoverHikvisionChannels_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isDiscoveringHikvisionChannels || _editingCamera is null)
        {
            return;
        }

        try
        {
            if (GetSelectedSourceKind() != CameraSourceKind.HikvisionRecorder)
            {
                throw new ArgumentException("请先把当前视频来源设为“海康威视 NVR / DVR”");
            }

            SaveCurrentCameraProfile();
            _isDiscoveringHikvisionChannels = true;
            DiscoverHikvisionChannelsButton.IsEnabled = false;
            HikvisionDiscoveryProgress.Visibility = Visibility.Visible;
            HikvisionDiscoveryStatusText.Text = "正在登录录像机并读取实际发布的通道…";

            var request = new HikvisionDiscoveryRequest(
                _editingCamera.HikvisionHost,
                _editingCamera.HikvisionHttpPort,
                _editingCamera.NetworkUsername,
                CameraCredentialProtector.Unprotect(_editingCamera.NetworkPasswordProtected));
            var channels = await _hikvisionChannelDiscovery.DiscoverAsync(
                request,
                _cameraDiscoveryLifetime.Token);
            if (channels.Count == 0)
            {
                HikvisionDiscoveryStatusText.Text = "录像机没有返回可用通道，请先在录像机中接入并启用 IPC。";
                return;
            }

            var result = HikvisionChannelProfileMerger.Merge(
                _cameraProfiles,
                _editingCamera,
                channels,
                HikvisionPreferSubStreamCheck.IsChecked == true);
            NormalizeCameraOrder();
            CameraRigList.Items.Refresh();
            _source.CameraRig.LastPerformanceTestPassed = false;
            if (_cameraProfiles.Count(camera => camera.Enabled) > 1)
            {
                ApplyCameraRigMode(CameraRigMode.MultiCamera);
            }
            CameraRigList.SelectedItem = _editingCamera;
            UpdateRigEstimate();

            var skippedText = result.SkippedChannels.Count == 0
                ? string.Empty
                : $"；受4机位上限影响，未添加通道 {string.Join("、", result.SkippedChannels)}";
            HikvisionDiscoveryStatusText.Text =
                $"发现 {result.DiscoveredCount} 路：新增 {result.AddedCount} 路，已有 {result.ExistingCount} 路{skippedText}。保存前请测试全部机位。";
            if (result.SkippedChannels.Count > 0)
            {
                MessageBox.Show(
                    this,
                    $"录像机共发现 {result.DiscoveredCount} 路，但软件最多保留4个机位。\n\n请删除不需要的 USB/IPC 机位后再次发现。",
                    "部分通道未添加",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (_cameraDiscoveryLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or
                                           InvalidOperationException or HttpRequestException or TaskCanceledException or
                                           InvalidDataException)
        {
            HikvisionDiscoveryStatusText.Text = $"通道发现失败：{exception.Message}";
            MessageBox.Show(this, HikvisionDiscoveryStatusText.Text, "海康通道发现", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isDiscoveringHikvisionChannels = false;
            HikvisionDiscoveryProgress.Visibility = Visibility.Collapsed;
            UpdateCameraSourceFields();
        }
    }

    private void SaveCurrentCameraProfile()
    {
        if (_loadingCamera || _editingCamera is null) return;
        var before = CameraProfileSignature(_editingCamera);
        _editingCamera.DisplayName = string.IsNullOrWhiteSpace(CameraNameInput.Text) ? "机位" : CameraNameInput.Text.Trim();
        _editingCamera.SourceType = (CameraSourceType)GetSelectedSourceKind();
        _editingCamera.LegacyCameraIndex = CameraIndexInput.SelectedItem is ComboBoxItem { Tag: int cameraIndex } ? cameraIndex : 0;
        _editingCamera.WindowsSymbolicLink = _cameraDevices.GetValueOrDefault(_editingCamera.LegacyCameraIndex)?.SymbolicLink ?? _editingCamera.WindowsSymbolicLink;
        _editingCamera.AutoSelectBestCamera = GetSelectedSourceKind() == CameraSourceKind.AutoLocal;
        _editingCamera.Width = ParseInt(WidthInput.Text, "相机宽度", 160, 8192);
        _editingCamera.Height = ParseInt(HeightInput.Text, "相机高度", 120, 8192);
        _editingCamera.FramesPerSecond = ParseDouble(FpsInput.Text, "帧率", 1, 120);
        _editingCamera.AutoFocus = AutoFocusCheck.IsChecked == true;
        _editingCamera.Brightness = BrightnessSlider.Value;
        _editingCamera.Contrast = ContrastSlider.Value;
        _editingCamera.Sharpness = SharpnessSlider.Value;
        _editingCamera.Saturation = SaturationSlider.Value;
        _editingCamera.NetworkStreamUrl = NetworkStreamUrlInput.Text.Trim();
        _editingCamera.NetworkUsername = NetworkUsernameInput.Text.Trim();
        _editingCamera.NetworkPasswordProtected = CameraCredentialProtector.Protect(NetworkPasswordInput.Password);
        _editingCamera.HikvisionHost = HikvisionHostInput.Text.Trim();
        _editingCamera.HikvisionHttpPort = ParseInt(HikvisionHttpPortInput.Text, "海康管理端口", 1, 65535);
        _editingCamera.HikvisionRtspPort = ParseInt(HikvisionPortInput.Text, "海康 RTSP 端口", 1, 65535);
        _editingCamera.HikvisionChannel = ParseInt(HikvisionChannelInput.Text, "海康通道号", 1, 999);
        _editingCamera.HikvisionSubStream = HikvisionStreamInput.SelectedIndex == 1;
        if (!string.Equals(before, CameraProfileSignature(_editingCamera), StringComparison.Ordinal))
        {
            _source.CameraRig.LastPerformanceTestPassed = false;
        }
        CameraRigList.Items.Refresh();
    }

    private void LoadCameraProfile(CameraProfile camera)
    {
        _loadingCamera = true;
        try
        {
            CameraNameInput.Text = camera.DisplayName;
            CameraSourceKindInput.SelectedIndex = (int)camera.SourceType;
            CameraIndexInput.SelectedItem = CameraIndexInput.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is int index &&
                    (!string.IsNullOrWhiteSpace(camera.WindowsSymbolicLink)
                        ? string.Equals(_cameraDevices.GetValueOrDefault(index)?.SymbolicLink, camera.WindowsSymbolicLink, StringComparison.OrdinalIgnoreCase)
                        : index == camera.LegacyCameraIndex));
            if (CameraIndexInput.SelectedIndex < 0 && CameraIndexInput.Items.Count > 0) CameraIndexInput.SelectedIndex = 0;
            AutoBestCameraCheck.IsChecked = camera.AutoSelectBestCamera;
            NetworkStreamUrlInput.Text = camera.NetworkStreamUrl;
            NetworkUsernameInput.Text = camera.NetworkUsername;
            NetworkPasswordInput.Password = CameraCredentialProtector.Unprotect(camera.NetworkPasswordProtected);
            HikvisionHostInput.Text = camera.HikvisionHost;
            HikvisionHttpPortInput.Text = camera.HikvisionHttpPort.ToString();
            HikvisionPortInput.Text = camera.HikvisionRtspPort.ToString();
            HikvisionChannelInput.Text = camera.HikvisionChannel.ToString();
            HikvisionStreamInput.SelectedIndex = camera.HikvisionSubStream ? 1 : 0;
            WidthInput.Text = camera.Width.ToString();
            HeightInput.Text = camera.Height.ToString();
            FpsInput.Text = camera.FramesPerSecond.ToString("0.##");
            AutoFocusCheck.IsChecked = camera.AutoFocus;
            BrightnessSlider.Value = camera.Brightness;
            ContrastSlider.Value = camera.Contrast;
            SharpnessSlider.Value = camera.Sharpness;
            SaturationSlider.Value = camera.Saturation;
            UpdateCameraSourceFields();
        }
        finally
        {
            _loadingCamera = false;
        }
    }

    private void NormalizeCameraOrder()
    {
        for (var index = 0; index < _cameraProfiles.Count; index++) _cameraProfiles[index].SortOrder = index;
    }

    private void UpdateRigEstimate()
    {
        var totalMegabits = GetActiveCameraProfiles()
            .Sum(camera => Math.Max(2, camera.Width * camera.Height * camera.FramesPerSecond * 0.08 / 1_000_000d)) + 8;
        var gigabytesPerHour = totalMegabits * 3600 / 8 / 1024;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(RecordingRootInput.Text));
            var freeGigabytes = string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
            var hours = gigabytesPerHour <= 0 ? 0 : freeGigabytes * 0.75 / gigabytesPerHour;
            CameraRigStatusText.Text = $"预计总码率 {totalMegabits:F1} Mbps · {gigabytesPerHour:F1} GB/小时 · 当前磁盘约可录 {hours:F1} 小时";
        }
        catch
        {
            CameraRigStatusText.Text = $"预计总码率 {totalMegabits:F1} Mbps · {gigabytesPerHour:F1} GB/小时";
        }
    }

    private static CameraProfile CloneCameraProfile(CameraProfile camera) => new()
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

    private static CameraOptions ToCameraOptions(CameraProfile camera) => new()
    {
        SourceKind = (CameraSourceKind)camera.SourceType,
        CameraIndex = camera.LegacyCameraIndex,
        WindowsSymbolicLink = camera.WindowsSymbolicLink,
        AutoSelectBestCamera = camera.AutoSelectBestCamera,
        Width = camera.Width,
        Height = camera.Height,
        FramesPerSecond = camera.FramesPerSecond,
        Codec = camera.Codec,
        AutoFocus = camera.AutoFocus,
        Brightness = camera.Brightness,
        Contrast = camera.Contrast,
        Sharpness = camera.Sharpness,
        Saturation = camera.Saturation,
        NetworkStreamUrl = camera.NetworkStreamUrl,
        NetworkUsername = camera.NetworkUsername,
        NetworkPasswordProtected = camera.NetworkPasswordProtected,
        HikvisionHost = camera.HikvisionHost,
        HikvisionHttpPort = camera.HikvisionHttpPort,
        HikvisionRtspPort = camera.HikvisionRtspPort,
        HikvisionChannel = camera.HikvisionChannel,
        HikvisionSubStream = camera.HikvisionSubStream
    };

    private static string CameraProfileSignature(CameraProfile camera) => string.Join('|',
        camera.DisplayName,
        camera.SourceType,
        camera.WindowsSymbolicLink,
        camera.LegacyCameraIndex,
        camera.Width,
        camera.Height,
        camera.FramesPerSecond,
        camera.RotationQuarterTurns,
        camera.Mirror,
        camera.NetworkStreamUrl,
        camera.NetworkUsername,
        camera.NetworkPasswordProtected,
        camera.HikvisionHost,
        camera.HikvisionHttpPort,
        camera.HikvisionRtspPort,
        camera.HikvisionChannel,
        camera.HikvisionSubStream);

    private CameraSourceKind GetSelectedSourceKind()
    {
        if (CameraSourceKindInput.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<CameraSourceKind>(tag, out var kind))
        {
            return kind;
        }
        return CameraSourceKind.AutoLocal;
    }

    private void UpdateCameraSourceFields()
    {
        if (!IsInitialized)
        {
            return;
        }
        var kind = GetSelectedSourceKind();
        var isLocal = kind is CameraSourceKind.AutoLocal or CameraSourceKind.WindowsCamera;
        var isCustomNetwork = kind == CameraSourceKind.NetworkStream;
        var isHikvision = kind == CameraSourceKind.HikvisionRecorder;
        var isNetwork = isCustomNetwork || isHikvision;

        LocalCameraLabel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        LocalCameraPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        AutoBestCameraCheck.Visibility = kind == CameraSourceKind.AutoLocal ? Visibility.Visible : Visibility.Collapsed;
        AutoFocusCheck.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        NetworkConnectionCard.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;
        NetworkStreamLabel.Visibility = isCustomNetwork ? Visibility.Visible : Visibility.Collapsed;
        NetworkStreamUrlInput.Visibility = isCustomNetwork ? Visibility.Visible : Visibility.Collapsed;
        HikvisionRecorderLabel.Visibility = isHikvision ? Visibility.Visible : Visibility.Collapsed;
        HikvisionRecorderPanel.Visibility = isHikvision ? Visibility.Visible : Visibility.Collapsed;
        NetworkCredentialsLabel.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;
        NetworkCredentialsPanel.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;
        HikvisionDiscoveryLabel.Visibility = isHikvision ? Visibility.Visible : Visibility.Collapsed;
        HikvisionDiscoveryPanel.Visibility = isHikvision ? Visibility.Visible : Visibility.Collapsed;

        CameraIndexInput.IsEnabled = isLocal;
        AutoBestCameraCheck.IsEnabled = kind == CameraSourceKind.AutoLocal;
        NetworkStreamUrlInput.IsEnabled = isCustomNetwork;
        HikvisionHostInput.IsEnabled = isHikvision;
        HikvisionHttpPortInput.IsEnabled = isHikvision;
        HikvisionPortInput.IsEnabled = isHikvision;
        HikvisionChannelInput.IsEnabled = isHikvision;
        HikvisionStreamInput.IsEnabled = isHikvision;
        NetworkUsernameInput.IsEnabled = isNetwork;
        NetworkPasswordInput.IsEnabled = isNetwork;
        DiscoverHikvisionChannelsButton.IsEnabled = isHikvision && !_isDiscoveringHikvisionChannels;
        HikvisionPreferSubStreamCheck.IsEnabled = isHikvision && !_isDiscoveringHikvisionChannels;
    }

    private static void ValidateCameraSource(CameraOptions camera)
    {
        if (camera.SourceKind == CameraSourceKind.NetworkStream)
        {
            CameraSourceUrlBuilder.AddCredentials(camera.NetworkStreamUrl, camera.NetworkUsername, "");
        }
        else if (camera.SourceKind == CameraSourceKind.HikvisionRecorder)
        {
            CameraSourceUrlBuilder.BuildHikvisionRtspUrl(
                camera.HikvisionHost,
                camera.HikvisionRtspPort,
                camera.HikvisionChannel,
                camera.HikvisionSubStream);
        }
    }

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException($"{name}不能为空")
        : value.Trim();

    private static int ParseInt(string value, string name, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"{name}必须在 {minimum} 到 {maximum} 之间");

    private static double ParseDouble(string value, string name, double minimum, double maximum) =>
        double.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ArgumentException($"{name}必须在 {minimum} 到 {maximum} 之间");
}
