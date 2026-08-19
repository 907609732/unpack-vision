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
    public const int CurrentSchemaVersion = 3;
    public const int ProductMaximumCameraCount = 16;
    public const int LegacyCompatibilityMaximumCameraCount = 4;
    public const int MaximumEnabledCameras = ProductMaximumCameraCount;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "默认机位方案";
    public CameraRigMode Mode { get; set; } = CameraRigMode.SingleCamera;
    public List<CameraProfile> Cameras { get; set; } = [];
    public int CompositeWidth { get; set; } = 1920;
    public int CompositeHeight { get; set; } = 1080;
    public double CompositeFramesPerSecond { get; set; } = 15;
    /// <summary>
    /// Null preserves the compatible default: rigs with up to four cameras create a
    /// composite, while higher-count rigs protect the independent evidence streams.
    /// </summary>
    public bool? CompositeRecordingEnabled { get; set; }
    public string CapacityProfileId { get; set; } = "custom";
    public DateTimeOffset? LastPerformanceTestAt { get; set; }
    public bool LastPerformanceTestPassed { get; set; }
    public string LastPerformanceTestFingerprint { get; set; } = string.Empty;

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

    public bool EffectiveCompositeRecordingEnabled =>
        CompositeRecordingEnabled ?? EnabledCameras.Count <= 4;

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
    string? Message = null,
    double EncodingFramesPerSecond = 0,
    int EncodingQueueDepth = 0,
    double ProcessingLatencyMilliseconds = 0,
    string? EncoderName = null,
    bool HardwareAccelerated = false);

/// <summary>
/// Describes a tested product preset without coupling the domain model to a specific
/// media implementation. Adapters may translate the recommendation into GStreamer,
/// Media Foundation, or a compatible fallback pipeline.
/// </summary>
public sealed record CameraCapacityProfile(
    string Id,
    string DisplayName,
    int MaximumCameraCount,
    int RecommendedWidth,
    int RecommendedHeight,
    double RecordingFramesPerSecond,
    double PreviewFramesPerSecond,
    bool CompositeRecordingEnabledByDefault);

public static class CameraCapacityProfiles
{
    public static CameraCapacityProfile FourCameraHighQuality { get; } = new(
        "four-camera-high-quality",
        "4路高清",
        4,
        1920,
        1080,
        15,
        8,
        true);

    public static CameraCapacityProfile EightCameraBalanced { get; } = new(
        "eight-camera-balanced",
        "8路均衡",
        8,
        1280,
        720,
        15,
        6,
        false);

    public static CameraCapacityProfile SixteenCameraEfficient { get; } = new(
        "sixteen-camera-efficient",
        "16路节能",
        CameraRigOptions.ProductMaximumCameraCount,
        1280,
        720,
        10,
        5,
        false);

    public static IReadOnlyList<CameraCapacityProfile> BuiltIn { get; } =
    [
        FourCameraHighQuality,
        EightCameraBalanced,
        SixteenCameraEfficient
    ];
}

/// <summary>
/// Reports the media engine features verified on the current station. The product limit
/// and the measured machine limit are separate so low-end computers can reject an unsafe
/// rig without changing the saved camera model.
/// </summary>
public sealed record MediaEngineCapabilities(
    int MaximumTestedCameraCount,
    int MaximumSynchronizedPlaybackStreams,
    IReadOnlyList<string> AvailableHardwareEncoders,
    bool SupportsIsolatedCameraPipelines,
    bool SupportsRecoverableFragmentedMp4,
    bool SupportsCompositeRecording)
{
    public int EffectiveMaximumCameraCount => Math.Clamp(
        MaximumTestedCameraCount,
        0,
        CameraRigOptions.ProductMaximumCameraCount);
}

/// <summary>
/// A bounded point-in-time sample used by performance admission and diagnostics.
/// It intentionally contains no business identifiers, paths, or device credentials.
/// </summary>
public sealed record CameraPipelineMetrics(
    string CameraId,
    DateTimeOffset SampledAt,
    double CaptureFramesPerSecond,
    double EncodingFramesPerSecond,
    double PreviewFramesPerSecond,
    double DroppedFrameRatio,
    int EncodingQueueDepth,
    double ProcessingLatencyMilliseconds,
    double EstimatedMegabitsPerSecond,
    string? EncoderName = null,
    bool HardwareAccelerated = false);

public sealed record CameraRigPerformanceResult(
    bool Passed,
    IReadOnlyList<CameraPerformanceResult> Cameras,
    double EstimatedMegabytesPerHour,
    double AvailableRecordingHours,
    string Summary)
{
    public string ConfigurationFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<StoragePerformanceResult> StorageTargets { get; init; } = [];
}

public sealed record StoragePerformanceResult(
    string TargetId,
    string DisplayName,
    bool Passed,
    double WriteMegabytesPerSecond,
    string Message);

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
