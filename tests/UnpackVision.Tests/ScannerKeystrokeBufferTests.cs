using UnpackVision.App;

namespace UnpackVision.Tests;

public sealed class ScannerKeystrokeBufferTests
{
    [Fact]
    public void CrLfStyleDoubleTerminatorEmitsBarcodeOnlyOnce()
    {
        var buffer = new ScannerKeystrokeBuffer();
        Append(buffer, "TEST-PARCEL-ALPHA", 100);

        var first = buffer.Complete(200);
        var lineFeedEquivalent = buffer.Complete(201);
        Append(buffer, "TEST-PARCEL-BETA", 210);
        var next = buffer.Complete(300);

        Assert.Equal("TEST-PARCEL-ALPHA", first);
        Assert.Null(lineFeedEquivalent);
        Assert.Equal("TEST-PARCEL-BETA", next);
    }

    [Fact]
    public void IdleGapDropsUnterminatedCharactersBeforeNextScan()
    {
        var buffer = new ScannerKeystrokeBuffer();
        Append(buffer, "STALE-PARCEL", 100);
        var nextStart = 100 + "STALE-PARCEL".Length +
            ScannerKeystrokeBuffer.MaximumInterKeyDelayMilliseconds + 1;

        Append(buffer, "TEST-PARCEL-BETA", nextStart);
        var completed = buffer.Complete(nextStart + 100);

        Assert.Equal("TEST-PARCEL-BETA", completed);
    }

    [Fact]
    public void AcceptedFallbackCanDiscardUnterminatedRawScanBeforeNextParcel()
    {
        var buffer = new ScannerKeystrokeBuffer();
        Append(buffer, "TEST-PARCEL-ALPHA", 100);

        buffer.Discard();
        Append(buffer, "TEST-PARCEL-BETA", 200);
        var completed = buffer.Complete(300);

        Assert.Equal("TEST-PARCEL-BETA", completed);
    }

    [Fact]
    public void RawCompletionBeforeLegacyEnterSuppressesFallbackCopy()
    {
        var gate = new ScannerInputSourceGate();
        gate.ObserveRaw(1_000);

        var fallback = gate.BeginFallback(1_001);

        Assert.False(gate.ShouldProcessFallback(fallback, 1_075));
    }

    [Fact]
    public void RawCompletionAfterLegacyEnterSuppressesDeferredFallbackCopy()
    {
        var gate = new ScannerInputSourceGate();
        var fallback = gate.BeginFallback(1_000);

        gate.ObserveRaw(1_001);

        Assert.False(gate.ShouldProcessFallback(fallback, 1_075));
    }

    [Fact]
    public void ManualFallbackWithoutRawInputRemainsAvailable()
    {
        var gate = new ScannerInputSourceGate();

        var fallback = gate.BeginFallback(1_000);

        Assert.True(gate.ShouldProcessFallback(fallback, 1_075));
    }

    [Fact]
    public void LegacyEnterWithinRawMatchingWindowConsumesItsRawCompletion()
    {
        var gate = new ScannerInputSourceGate();
        gate.ObserveRaw(1_000);

        var delayedFallback = gate.BeginFallback(1_400);

        Assert.False(gate.ShouldProcessFallback(delayedFallback, 1_475));
    }

    [Fact]
    public void ConsecutiveRawScansSuppressBothQueuedLegacyFallbacks()
    {
        var gate = new ScannerInputSourceGate();
        gate.ObserveRaw(1_000);
        gate.ObserveRaw(1_100);

        var firstQueuedFallback = gate.BeginFallback(1_200);
        var secondQueuedFallback = gate.BeginFallback(1_300);
        var laterManualFallback = gate.BeginFallback(1_400);

        Assert.False(gate.ShouldProcessFallback(firstQueuedFallback, 1_275));
        Assert.False(gate.ShouldProcessFallback(secondQueuedFallback, 1_375));
        Assert.True(gate.ShouldProcessFallback(laterManualFallback, 1_475));
    }

    [Fact]
    public void ExpiredUnmatchedRawDoesNotBlockLaterManualFallback()
    {
        var gate = new ScannerInputSourceGate();
        gate.ObserveRaw(1_000);
        var later = 1_000 + ScannerInputSourceGate.UnmatchedRawLifetimeMilliseconds + 1;

        var manualFallback = gate.BeginFallback(later);

        Assert.True(gate.ShouldProcessFallback(manualFallback, later + 75));
    }

    [Fact]
    public void ManualFallbackSixHundredMillisecondsAfterRawIsAllowed()
    {
        var gate = new ScannerInputSourceGate();
        gate.ObserveRaw(1_000);

        var manualFallback = gate.BeginFallback(1_600);

        Assert.True(gate.ShouldProcessFallback(manualFallback, 1_675));
    }

    private static void Append(ScannerKeystrokeBuffer buffer, string value, long startedAt)
    {
        for (var index = 0; index < value.Length; index++)
        {
            buffer.Append(value[index], startedAt + index);
        }
    }
}
