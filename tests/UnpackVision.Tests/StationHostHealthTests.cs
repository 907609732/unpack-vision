using System.Net;
using Microsoft.AspNetCore.Http;
using UnpackVision.StationHost;

namespace UnpackVision.Tests;

public sealed class StationHostHealthTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 10, 10, 30, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.0.2.10", false)]
    public void RequestOriginDeterminesWhetherDiagnosticsAreTrusted(string remoteAddress, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);

        Assert.Equal(expected, StationHostEndpointSupport.IsLoopback(context));
    }

    [Fact]
    public void LanHealthPayloadExposesOnlyStatusAndVersion()
    {
        var payload = StationHostEndpointSupport.BuildHealthPayload(
            isLoopback: false,
            version: "2.5.2",
            tlsEnabled: true,
            lanAddresses: [IPAddress.Parse("192.0.2.10")],
            currentTime: FixedTime);

        Assert.Equal(["status", "version"], payload.Keys);
        Assert.Equal("healthy", payload["status"]);
        Assert.Equal("2.5.2", payload["version"]);
        Assert.DoesNotContain("tls", payload.Keys);
        Assert.DoesNotContain("lanAddresses", payload.Keys);
        Assert.DoesNotContain("time", payload.Keys);
    }

    [Fact]
    public void LoopbackHealthPayloadKeepsDesktopDiagnostics()
    {
        var addresses = new[]
        {
            IPAddress.Parse("192.0.2.10"),
            IPAddress.Parse("198.51.100.5")
        };

        var payload = StationHostEndpointSupport.BuildHealthPayload(
            isLoopback: true,
            version: "2.5.2",
            tlsEnabled: true,
            lanAddresses: addresses,
            currentTime: FixedTime);

        Assert.Equal(["status", "version", "tls", "lanAddresses", "time"], payload.Keys);
        Assert.Equal("healthy", payload["status"]);
        Assert.Equal("2.5.2", payload["version"]);
        Assert.Equal(true, payload["tls"]);
        Assert.Equal(
            ["192.0.2.10", "198.51.100.5"],
            Assert.IsType<string[]>(payload["lanAddresses"]));
        Assert.Equal(FixedTime, payload["time"]);
    }
}
