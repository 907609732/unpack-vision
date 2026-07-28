using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraSelectionTests
{
    [Fact]
    public async Task Phone_publish_session_can_replace_idle_recording_source_without_opening_camera()
    {
        var activeOptions = new CameraOptions();
        var desiredOptions = new CameraOptions
        {
            SourceKind = CameraSourceKind.NetworkStream,
            NetworkStreamUrl = "rtsp://192.168.31.100:8554/device/test-phone",
            NetworkUsername = "test-phone",
            NetworkPasswordProtected = CameraCredentialProtector.Protect("temporary-token"),
            Width = 1080,
            Height = 1920,
            FramesPerSecond = 15
        };

        await using var backend = new OpenCvRecordingBackend(new StorageOptions(), activeOptions);
        await backend.ConfigureCameraAsync(desiredOptions, restartPreview: false);

        Assert.Equal(CameraSourceKind.NetworkStream, activeOptions.SourceKind);
        Assert.Equal(desiredOptions.NetworkStreamUrl, activeOptions.NetworkStreamUrl);
        Assert.Equal("test-phone", activeOptions.NetworkUsername);
        Assert.Equal("temporary-token", CameraCredentialProtector.Unprotect(activeOptions.NetworkPasswordProtected));
        Assert.Equal(1080, activeOptions.Width);
        Assert.Equal(1920, activeOptions.Height);
        Assert.Equal(15, activeOptions.FramesPerSecond);
    }

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
