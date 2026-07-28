using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class PairedDeviceRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionDevices-{Guid.NewGuid():N}");

    [Fact]
    public async Task PairingPersistsAndAuthenticatesOnlyGrantedScope()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.FromHours(8)));
        var options = new StorageOptions { DatabasePath = Path.Combine(_root, "devices.db") };
        var registry = new SqlitePairedDeviceRegistry(options, clock);
        await registry.InitializeAsync();

        var credential = await registry.PairAsync(new DeviceRegistration(
            "旧手机扫码器",
            "test-public-key",
            ["scanner"],
            ["scan:send"]));

        var reloaded = new SqlitePairedDeviceRegistry(options, clock);
        await reloaded.InitializeAsync();
        var authenticated = await reloaded.AuthenticateAsync(
            credential.Device.Id,
            credential.AccessToken,
            "scan:send");

        Assert.NotNull(authenticated);
        Assert.Equal("旧手机扫码器", authenticated.Name);
        Assert.Null(await reloaded.AuthenticateAsync(
            credential.Device.Id,
            credential.AccessToken,
            "video:read"));
        Assert.Null(await reloaded.AuthenticateAsync(
            credential.Device.Id,
            "wrong-token",
            "scan:send"));
        Assert.Single(await reloaded.GetAllAsync());
    }

    [Fact]
    public async Task DeletedDeviceCannotAuthenticateOrRemainVisible()
    {
        var clock = new FakeClock(DateTimeOffset.Now);
        var registry = new SqlitePairedDeviceRegistry(
            new StorageOptions { DatabasePath = Path.Combine(_root, "revoked.db") },
            clock);
        await registry.InitializeAsync();
        var credential = await registry.PairAsync(new DeviceRegistration(
            "摄像手机",
            "test-public-key",
            ["camera"],
            ["camera:publish", "scan:send"]));

        Assert.True(await registry.RevokeAsync(credential.Device.Id, clock.Now.AddMinutes(1)));
        Assert.False(await registry.RevokeAsync(credential.Device.Id, clock.Now.AddMinutes(2)));
        Assert.Null(await registry.AuthenticateAsync(
            credential.Device.Id,
            credential.AccessToken,
            "scan:send"));
        Assert.True(await registry.DeleteAsync(credential.Device.Id));
        Assert.Empty(await registry.GetAllAsync());
        Assert.Null(await registry.AuthenticateAsync(
            credential.Device.Id,
            credential.AccessToken,
            "scan:send"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
