namespace UnpackVision.Core;

public enum CameraSourceType
{
    AutoLocal,
    WindowsCamera,
    NetworkStream,
    HikvisionRecorder
}

public enum CameraConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Reconnecting,
    Missing,
    Failed
}

/// <summary>
/// Controls whether the station operates only its primary camera or the whole saved rig.
/// Profiles are retained when switching to single-camera mode so users can return to a
/// multi-camera setup without configuring every source again.
/// </summary>
public enum CameraRigMode
{
    SingleCamera,
    MultiCamera
}

public sealed class CameraRigOptions
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumEnabledCameras = 4;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "默认机位方案";
    public CameraRigMode Mode { get; set; } = CameraRigMode.SingleCamera;
    public List<CameraProfile> Cameras { get; set; } = [];
    public int CompositeWidth { get; set; } = 1920;
    public int CompositeHeight { get; set; } = 1080;
    public double CompositeFramesPerSecond { get; set; } = 15;
    public DateTimeOffset? LastPerformanceTestAt { get; set; }
    public bool LastPerformanceTestPassed { get; set; }

    public IReadOnlyList<CameraProfile> EnabledCameras
    {
        get
        {
            var enabled = Cameras
                .Where(camera => camera.Enabled)
                .OrderBy(camera => camera.SortOrder)
                .Take(MaximumEnabledCameras)
                .ToArray();
            if (Mode == CameraRigMode.MultiCamera)
            {
                return enabled;
            }

            var primary = enabled.FirstOrDefault(camera => camera.IsPrimary) ?? enabled.FirstOrDefault();
            return primary is null ? [] : [primary];
        }
    }

    public CameraProfile? PrimaryCamera => EnabledCameras.FirstOrDefault(camera => camera.IsPrimary);

    /// <summary>
    /// Returns every configured source that can be selected from the desktop camera picker.
    /// Single-camera mode keeps inactive angles available for fast source switching, while
    /// multi-camera mode exposes only the pipelines that are part of the active rig.
    /// </summary>
    public IReadOnlyList<CameraProfile> GetSelectableCameras() => Mode == CameraRigMode.SingleCamera
        ? Cameras
            .Where(camera => camera.Enabled)
            .OrderBy(camera => camera.SortOrder)
            .Take(MaximumEnabledCameras)
            .ToArray()
        : EnabledCameras;

    /// <summary>
    /// Promotes a saved enabled profile to the only active primary source in single-camera mode.
    /// Profiles remain enabled so switching does not destroy a previously tested multi-camera rig.
    /// </summary>
    public bool TrySelectSingleCamera(string cameraId)
    {
        if (Mode != CameraRigMode.SingleCamera || string.IsNullOrWhiteSpace(cameraId))
        {
            return false;
        }

        var target = Cameras.FirstOrDefault(camera =>
            camera.Enabled && string.Equals(camera.Id, cameraId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        foreach (var camera in Cameras)
        {
            camera.IsPrimary = ReferenceEquals(camera, target);
        }
        return true;
    }
}

public sealed class CameraProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "主机位";
    public bool Enabled { get; set; } = true;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
    public CameraSourceType SourceType { get; set; } = CameraSourceType.AutoLocal;
    public string WindowsSymbolicLink { get; set; } = string.Empty;
    public int LegacyCameraIndex { get; set; }
    public bool AutoSelectBestCamera { get; set; } = true;
    public int Width { get; set; } = 3840;
    public int Height { get; set; } = 2160;
    public double FramesPerSecond { get; set; } = 15;
    public string Codec { get; set; } = "mp4v";
    public int RotationQuarterTurns { get; set; }
    public bool Mirror { get; set; }
    public double Brightness { get; set; } = 50;
    public double Contrast { get; set; } = 50;
    public double Sharpness { get; set; } = 50;
    public double Saturation { get; set; } = 50;
    public bool AutoFocus { get; set; } = true;
    public string NetworkStreamUrl { get; set; } = string.Empty;
    public string NetworkUsername { get; set; } = string.Empty;
    public string NetworkPasswordProtected { get; set; } = string.Empty;
    public string HikvisionHost { get; set; } = string.Empty;
    public int HikvisionHttpPort { get; set; } = 80;
    public int HikvisionRtspPort { get; set; } = 554;
    public int HikvisionChannel { get; set; } = 1;
    public bool HikvisionSubStream { get; set; }
}

public sealed record CameraRuntimeState(
    string CameraId,
    string DisplayName,
    bool IsPrimary,
    CameraConnectionState ConnectionState,
    int Width,
    int Height,
    double FramesPerSecond,
    double DroppedFrameRatio,
    string? Message = null);

public sealed record CameraRigPerformanceResult(
    bool Passed,
    IReadOnlyList<CameraPerformanceResult> Cameras,
    double EstimatedMegabytesPerHour,
    double AvailableRecordingHours,
    string Summary);

public sealed record CameraPerformanceResult(
    string CameraId,
    string DisplayName,
    bool Passed,
    double TargetFramesPerSecond,
    double ActualFramesPerSecond,
    double DroppedFrameRatio,
    double EstimatedMegabitsPerSecond,
    string Message);

/// <summary>
/// Describes user-facing progress for the fixed camera performance test workflow.
/// Percent is monotonic from 0 to 100; remaining seconds are supplied only for timed stages.
/// </summary>
public sealed record CameraPerformanceProgress(
    int Percent,
    string Stage,
    int? SecondsRemaining = null);
