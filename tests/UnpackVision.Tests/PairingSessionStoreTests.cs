using UnpackVision.StationHost;

namespace UnpackVision.Tests;

public sealed class PairingSessionStoreTests
{
    [Fact]
    public void WrongTokenDoesNotConsumeValidSession()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.FromHours(8)));
        var store = new PairingSessionStore(clock, new StationHostOptions
        {
            StationId = "station-test",
            PairingLifetimeMinutes = 5
        });
        var created = store.Create(new Uri("http://192.168.31.100:5271"));

        Assert.False(store.TryConsume(created.Id, "wrong-token", out _));
        Assert.True(store.TryConsume(created.Id, created.Token, out var consumed));
        Assert.Equal(created, consumed);
        Assert.False(store.TryConsume(created.Id, created.Token, out _));
    }

    [Fact]
    public void ExpiredSessionCannotBeConsumed()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.FromHours(8)));
        var store = new PairingSessionStore(clock, new StationHostOptions
        {
            StationId = "station-test",
            PairingLifetimeMinutes = 5
        });
        var created = store.Create(new Uri("http://192.168.31.100:5271"));

        clock.Now = clock.Now.AddMinutes(6);

        Assert.False(store.TryConsume(created.Id, created.Token, out _));
    }

    [Fact]
    public void FiveWrongTokensLockPairingSession()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.FromHours(8)));
        var store = new PairingSessionStore(clock, new StationHostOptions
        {
            StationId = "station-test",
            PairingLifetimeMinutes = 5
        });
        var created = store.Create(new Uri("https://192.168.31.100:5273"));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(store.TryConsume(created.Id, $"wrong-{attempt}", out _));
        }

        Assert.False(store.TryConsume(created.Id, created.Token, out _));
    }
}
