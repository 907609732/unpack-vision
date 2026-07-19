using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using UnpackVision.Infrastructure;

namespace UnpackVision.App;

public partial class SettingsWindow : Window
{
    private readonly LocalSettings _source;
    private readonly LoudSpeechService _speechTest = new();

    public SettingsWindow(LocalSettings settings, bool showCameraTab = false)
    {
        InitializeComponent();
        Closed += (_, _) => _speechTest.Dispose();
        _source = settings;
        RecordingRootInput.Text = settings.RecordingRoot;
        ExcelPathInput.Text = settings.ExcelWorkbookPath;
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
        UpdateCameraSourceFields();
        if (showCameraTab)
        {
            SettingsTabControl.SelectedIndex = 1;
        }
    }

    public LocalSettings? SavedSettings { get; private set; }

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
