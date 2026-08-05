namespace UnpackVision.Infrastructure;

public enum CameraSourceKind
{
    AutoLocal,
    WindowsCamera,
    NetworkStream,
    HikvisionRecorder
}

public sealed class StorageOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "unpackvision.db");

    public string RecordingRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "UnpackVision");
}

public sealed class ExcelConnectorOptions
{
    public string WorkbookPath { get; set; } = string.Empty;
    public string WorksheetName { get; set; } = "退货扫码单号";
    public string BackupRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "Backups");
    public string ConnectorId { get; set; } = "excel";
}

public sealed class HikCompatibilityOptions
{
    public string UnpackingDirectory { get; set; } =
        @"D:\Program Files\海康威视\HIK SCAN\storage\LogisticsUnpacking";
    public string PackingDirectory { get; set; } =
        @"D:\Program Files\海康威视\HIK SCAN\storage\LogisticsPaking";
    public int StableSamples { get; set; } = 3;
    public int StableSampleDelayMilliseconds { get; set; } = 1000;
    public int FinalizationTimeoutSeconds { get; set; } = 180;
}

public sealed class CameraOptions
{
    public CameraSourceKind SourceKind { get; set; } = CameraSourceKind.AutoLocal;
    public int CameraIndex { get; set; }
    public bool AutoSelectBestCamera { get; set; } = true;
    public int ProbeCameraCount { get; set; } = 6;
    public double MinimumResolutionRatio { get; set; } = 0.9;
    public int Width { get; set; } = 3840;
    public int Height { get; set; } = 2160;
    public double FramesPerSecond { get; set; } = 15;
    public string Codec { get; set; } = "mp4v";
    public double Brightness { get; set; } = 50;
    public double Contrast { get; set; } = 50;
    public double Sharpness { get; set; } = 50;
    public double Saturation { get; set; } = 50;
    public bool AutoFocus { get; set; } = true;
    public string NetworkStreamUrl { get; set; } = string.Empty;
    public string NetworkUsername { get; set; } = string.Empty;
    public string NetworkPasswordProtected { get; set; } = string.Empty;
    public string HikvisionHost { get; set; } = string.Empty;
    public int HikvisionRtspPort { get; set; } = 554;
    public int HikvisionChannel { get; set; } = 1;
    public bool HikvisionSubStream { get; set; }
}

public sealed class SecurityOptions
{
    public string ApiKeyPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "api-key.protected");
}

public sealed class WebhookOptions
{
    public List<string> Endpoints { get; set; } = [];
    public string Secret { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class MediaRelayOptions
{
    public string Version { get; set; } = "1.18.2";
    public string ExecutablePath { get; set; } = string.Empty;
    public string RuntimeDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "MediaMTX");
    public string AuthHttpAddress { get; set; } = "http://127.0.0.1:5271/internal/media/auth";
    public string ControlApiAddress { get; set; } = "http://127.0.0.1:9997";
    public string CertificatePath { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public int RtspPort { get; set; } = 8554;
    public int RtspsPort { get; set; } = 8555;
    public int WebRtcPort { get; set; } = 8889;
    public int WebRtcUdpPort { get; set; } = 8189;
}
