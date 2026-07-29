using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class MediaRelayTests
{
    [Fact]
    public void Configuration_UsesAuthenticatedTlsRelayAndDisablesUnusedProtocols()
    {
        var options = new MediaRelayOptions
        {
            AuthHttpAddress = "http://127.0.0.1:5271/internal/media/auth",
            RtspsPort = 18322,
            WebRtcPort = 18889,
            CertificatePath = @"C:\certs\station.crt",
            PrivateKeyPath = @"C:\certs\station.key"
        };

        var configuration = MediaRelayConfiguration.Build(options);

        Assert.Contains("authMethod: http", configuration);
        Assert.Contains(options.AuthHttpAddress, configuration);
        Assert.Contains("rtspTransports: [tcp]", configuration);
        Assert.Contains("rtspEncryption: \"strict\"", configuration);
        Assert.Contains("rtspsAddress: :18322", configuration);
        Assert.Contains("webrtcAddress: :18889", configuration);
        Assert.Contains("webrtcEncryption: true", configuration);
        Assert.Contains("overridePublisher: false", configuration);
        Assert.Contains("hls: false", configuration);
        Assert.Contains("rtmp: false", configuration);
        Assert.Contains("srt: false", configuration);
    }

    [Fact]
    public async Task EndpointFactory_UsesDeviceScopedPaths()
    {
        var options = new MediaRelayOptions { RtspsPort = 8555, WebRtcPort = 8889 };
        await using var manager = new MediaRelayManager(options);

        var publish = manager.CreatePublishEndpoint("192.168.31.100", "device-01");
        var live = manager.CreateLiveEndpoint("192.168.31.100", "device-01", "viewer-02");

        Assert.Equal("device/device-01", publish.StreamPath);
        Assert.Equal("rtsps://192.168.31.100:8555/device/device-01", publish.RtspUrl.ToString().TrimEnd('/'));
        Assert.Equal("device-01", publish.AuthUser);
        Assert.Equal("https://192.168.31.100:8889/device/device-01/whep", live.WhepUrl.ToString());
        Assert.Equal("viewer-02", live.AuthUser);
    }

    [Theory]
    [InlineData("bad/device")]
    [InlineData("带中文")]
    [InlineData("")]
    public async Task EndpointFactory_RejectsUnsafeDeviceIds(string deviceId)
    {
        await using var manager = new MediaRelayManager(new MediaRelayOptions());
        Assert.Throws<ArgumentException>(() => manager.CreatePublishEndpoint("127.0.0.1", deviceId));
    }
}
