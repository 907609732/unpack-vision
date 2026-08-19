using UnpackVision.Core;

namespace UnpackVision.App;

internal sealed record CameraConfigurationPreviewFrame(
    byte[] JpegBytes,
    DateTimeOffset CapturedAt,
    int Width,
    int Height,
    double FramesPerSecond);

/// <summary>
/// Owns the station-wide camera hand-off required by a modal configuration preview. The
/// dialog only renders frames and never constructs or owns a concrete media backend.
/// </summary>
internal interface ICameraConfigurationPreviewHost
{
    Task RunCameraConfigurationPreviewAsync(
        CameraProfile profile,
        Action<CameraConfigurationPreviewFrame> onFrame,
        CancellationToken cancellationToken);
}
