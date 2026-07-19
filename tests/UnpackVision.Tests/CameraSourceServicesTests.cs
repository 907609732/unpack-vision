using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraSourceServicesTests
{
    [Fact]
    public void Builds_hikvision_nvr_main_and_sub_stream_urls()
    {
        Assert.Equal(
            "rtsp://user:p%40ss@192.168.1.8:554/Streaming/channels/1701",
            CameraSourceUrlBuilder.BuildHikvisionRtspUrl("192.168.1.8", 554, 17, false, "user", "p@ss"));
        Assert.Equal(
            "rtsp://192.168.1.8:554/Streaming/channels/102",
            CameraSourceUrlBuilder.BuildHikvisionRtspUrl("192.168.1.8", 554, 1, true));
    }

    [Fact]
    public void Camera_password_is_protected_for_current_windows_user()
    {
        var protectedValue = CameraCredentialProtector.Protect("secret-value");
        Assert.NotEqual("secret-value", protectedValue);
        Assert.Equal("secret-value", CameraCredentialProtector.Unprotect(protectedValue));
    }

    [Theory]
    [InlineData("rtsp://192.168.1.9/live")]
    [InlineData("http://192.168.1.9/video.mjpg")]
    public void Custom_stream_accepts_supported_protocols(string url)
    {
        Assert.StartsWith(url.Split(':')[0], CameraSourceUrlBuilder.AddCredentials(url, "", ""));
    }
}
