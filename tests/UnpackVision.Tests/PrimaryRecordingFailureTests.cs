using UnpackVision.App;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class PrimaryRecordingFailureTests
{
    [Fact]
    public void Backend_notification_gate_emits_only_once_while_recording()
    {
        var gate = new PrimaryRecordingFailureNotificationGate();

        Assert.True(gate.TryClaim(recordingIsStopping: false));
        Assert.False(gate.TryClaim(recordingIsStopping: false));
    }

    [Fact]
    public void Backend_notification_gate_does_not_emit_during_normal_stop()
    {
        var gate = new PrimaryRecordingFailureNotificationGate();

        Assert.False(gate.TryClaim(recordingIsStopping: true));
    }

    [Fact]
    public void Backend_notification_gate_is_single_fire_under_concurrency()
    {
        var gate = new PrimaryRecordingFailureNotificationGate();
        var claimed = 0;

        Parallel.For(0, 32, _ =>
        {
            if (gate.TryClaim(recordingIsStopping: false))
            {
                Interlocked.Increment(ref claimed);
            }
        });

        Assert.Equal(1, claimed);
    }

    [Theory]
    [InlineData(RecordingState.Idle)]
    [InlineData(RecordingState.Starting)]
    [InlineData(RecordingState.Saving)]
    [InlineData(RecordingState.Completed)]
    [InlineData(RecordingState.Failed)]
    public void Desktop_emergency_stop_gate_rejects_non_recording_states(RecordingState state)
    {
        var gate = new PrimaryRecordingEmergencyStopGate();

        Assert.False(gate.TryBegin(state));
    }

    [Fact]
    public void Desktop_emergency_stop_gate_prevents_reentry_and_resets_for_next_recording()
    {
        var gate = new PrimaryRecordingEmergencyStopGate();

        Assert.True(gate.TryBegin(RecordingState.Recording));
        Assert.False(gate.TryBegin(RecordingState.Recording));

        gate.Complete();

        Assert.True(gate.TryBegin(RecordingState.Recording));
    }
}
