using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class ScanCommandLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionLedger-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReceiptSurvivesLedgerRecreationAndIsNotOverwritten()
    {
        var options = new StorageOptions { DatabasePath = Path.Combine(_root, "ledger.db") };
        var clock = new FakeClock(new DateTimeOffset(2026, 7, 22, 13, 0, 0, TimeSpan.FromHours(8)));
        var firstLedger = new SqliteScanCommandLedger(options, clock);
        await firstLedger.InitializeAsync();
        var original = new ScanAcknowledgement(
            Guid.NewGuid(),
            ScanCommandAction.Collected,
            RecordingState.Collected,
            Guid.NewGuid(),
            "SF0012345678",
            "单号已收集并进入 Excel 队列",
            clock.Now);
        await firstLedger.SaveAsync("stable-event", original);
        await firstLedger.SaveAsync("stable-event", original with { Message = "不应覆盖" });

        var recreated = new SqliteScanCommandLedger(options, clock);
        await recreated.InitializeAsync();
        var restored = await recreated.GetAsync("stable-event");

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
        Assert.Null(await recreated.GetAsync("missing-event"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
