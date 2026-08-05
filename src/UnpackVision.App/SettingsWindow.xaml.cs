using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

    public SettingsWindow(LocalSettings settings, bool showCameraTab = false)
    {
        InitializeComponent();
        Closed += SettingsWindow_OnClosed;
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

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var minimum = ParseInt(MinimumLengthInput.Text, "最小单号长度", 1, 200);
            var maximum = ParseInt(MaximumLengthInput.Text, "最大单号长度", minimum, 200);
            SavedSettings = new LocalSettings
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
                Camera = new CameraOptions
                {
                    SourceKind = GetSelectedSourceKind(),
                    CameraIndex = CameraIndexInput.SelectedItem is ComboBoxItem { Tag: int cameraIndex } ? cameraIndex : 0,
                    AutoSelectBestCamera = GetSelectedSourceKind() == CameraSourceKind.AutoLocal,
                    ProbeCameraCount = _source.Camera.ProbeCameraCount,
                    MinimumResolutionRatio = _source.Camera.MinimumResolutionRatio,
                    Width = ParseInt(WidthInput.Text, "相机宽度", 160, 8192),
                    Height = ParseInt(HeightInput.Text, "相机高度", 120, 8192),
                    FramesPerSecond = ParseDouble(FpsInput.Text, "帧率", 1, 120),
                    Codec = _source.Camera.Codec,
                    AutoFocus = AutoFocusCheck.IsChecked == true,
                    Brightness = BrightnessSlider.Value,
                    Contrast = ContrastSlider.Value,
                    Sharpness = SharpnessSlider.Value,
                    Saturation = SaturationSlider.Value,
                    NetworkStreamUrl = NetworkStreamUrlInput.Text.Trim(),
                    NetworkUsername = NetworkUsernameInput.Text.Trim(),
                    NetworkPasswordProtected = CameraCredentialProtector.Protect(NetworkPasswordInput.Password),
                    HikvisionHost = HikvisionHostInput.Text.Trim(),
                    HikvisionRtspPort = ParseInt(HikvisionPortInput.Text, "海康 RTSP 端口", 1, 65535),
                    HikvisionChannel = ParseInt(HikvisionChannelInput.Text, "海康通道号", 1, 999),
                    HikvisionSubStream = HikvisionStreamInput.SelectedIndex == 1
                }
            };
            ValidateCameraSource(SavedSettings.Camera);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "配置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        _speechTest.Dispose();
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
        CameraIndexInput.IsEnabled = kind is CameraSourceKind.AutoLocal or CameraSourceKind.WindowsCamera;
        AutoBestCameraCheck.IsEnabled = kind == CameraSourceKind.AutoLocal;
        NetworkStreamUrlInput.IsEnabled = kind == CameraSourceKind.NetworkStream;
        HikvisionHostInput.IsEnabled = kind == CameraSourceKind.HikvisionRecorder;
        HikvisionPortInput.IsEnabled = kind == CameraSourceKind.HikvisionRecorder;
        HikvisionChannelInput.IsEnabled = kind == CameraSourceKind.HikvisionRecorder;
        HikvisionStreamInput.IsEnabled = kind == CameraSourceKind.HikvisionRecorder;
        var isNetwork = kind is CameraSourceKind.NetworkStream or CameraSourceKind.HikvisionRecorder;
        NetworkUsernameInput.IsEnabled = isNetwork;
        NetworkPasswordInput.IsEnabled = isNetwork;
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
