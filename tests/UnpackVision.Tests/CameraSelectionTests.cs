using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraSelectionTests
{
    [Theory]
    [InlineData(3840, 2160, 3840, 2160, true)]
    [InlineData(3500, 2000, 3840, 2160, true)]
    [InlineData(2560, 1440, 3840, 2160, false)]
    [InlineData(1920, 1080, 1920, 1080, true)]
    public void Resolution_matching_rejects_virtual_camera_below_requested_profile(
        int actualWidth,
        int actualHeight,
        int requestedWidth,
        int requestedHeight,
        bool expected)
    {
        Assert.Equal(expected, OpenCvRecordingBackend.IsRequestedResolutionSatisfied(
            actualWidth,
            actualHeight,
            requestedWidth,
            requestedHeight));
    }
}
