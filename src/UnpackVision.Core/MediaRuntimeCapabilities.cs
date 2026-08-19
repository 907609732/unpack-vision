namespace UnpackVision.Core;

public enum MediaRuntimeOrigin
{
    Unknown,
    Bundled,
    Environment,
    System,
    Path
}

public enum MediaEncoderFamily
{
    IntelQuickSync,
    NvidiaNvenc,
    AmdAmf,
    WindowsMediaFoundation,
    Software
}

/// <summary>
/// Describes an H.264 encoder element that was inspected in the selected GStreamer runtime.
/// Product selection is based on this result rather than GPU vendor or model-name guesses.
/// </summary>
public sealed record MediaEncoderCapability(
    MediaEncoderFamily Family,
    string ElementName,
    bool Available,
    bool HardwareAccelerated,
    bool ApprovedForBundledRedistribution,
    string? Message = null);

public sealed record GStreamerRuntimeCapabilities(
    bool RuntimeFound,
    bool IsRequiredVersion,
    string RequiredVersion,
    string? Version,
    MediaRuntimeOrigin Origin,
    string? InspectExecutablePath,
    string? LaunchExecutablePath,
    IReadOnlyDictionary<string, bool> RequiredElements,
    IReadOnlyList<MediaEncoderCapability> Encoders,
    string? Message = null)
{
    public bool HasAllRequiredElements =>
        RuntimeFound &&
        IsRequiredVersion &&
        RequiredElements.Count > 0 &&
        RequiredElements.All(element => element.Value);
}

/// <summary>
/// Reports whether a located FFmpeg binary is suitable for the project's LGPL-only
/// redistribution policy. A rejected binary may still be used for local diagnostics,
/// but must not be copied into a release package.
/// </summary>
public sealed record FfmpegRuntimeCapabilities(
    bool RuntimeFound,
    bool IsExpectedVersion,
    string ExpectedVersion,
    string? Version,
    MediaRuntimeOrigin Origin,
    string? ExecutablePath,
    bool GplEnabled,
    bool NonFreeEnabled,
    bool ApprovedForBundledRedistribution,
    string? Message = null);

public sealed record MediaRuntimeProbeResult(
    GStreamerRuntimeCapabilities GStreamer,
    FfmpegRuntimeCapabilities Ffmpeg);

public sealed record MediaEncoderSelection(
    bool Success,
    MediaEncoderCapability? Encoder,
    string Message);

/// <summary>
/// Capability boundary used by admission tests and media-engine composition. Implementations
/// must bound external process execution and return an unavailable result when runtimes are absent.
/// </summary>
public interface IMediaRuntimeCapabilityProbe
{
    Task<MediaRuntimeProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}
