using OpenCvSharp;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class CameraRuntimeMetricsTests
{
    [Fact]
    public void Idle_metrics_are_explicitly_empty()
    {
        var metrics = new CameraEncodingRuntimeMetrics();

        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.FramesPerSecond);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.Equal(0, snapshot.ProcessingLatencyMilliseconds);
        Assert.Null(snapshot.EncoderName);
        Assert.False(snapshot.HardwareAccelerated);
    }

    [Fact]
    public void Active_metrics_report_writer_identity_throughput_and_latest_processing_latency()
    {
        var metrics = new CameraEncodingRuntimeMetrics();
        var writer = new StubFrameWriter("nvh264enc", hardwareAccelerated: true);

        metrics.Begin(writer);
        Thread.Sleep(30);
        metrics.RecordWrite(TimeSpan.FromMilliseconds(4.25));
        metrics.RecordWrite(TimeSpan.FromMilliseconds(6.5));
        var snapshot = metrics.Snapshot();

        Assert.True(snapshot.FramesPerSecond > 0);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.Equal(6.5, snapshot.ProcessingLatencyMilliseconds, precision: 3);
        Assert.Equal("nvh264enc", snapshot.EncoderName);
        Assert.True(snapshot.HardwareAccelerated);
    }

    [Fact]
    public void Ending_a_recording_clears_previous_session_metrics()
    {
        var metrics = new CameraEncodingRuntimeMetrics();
        metrics.Begin(new StubFrameWriter("OpenCV compatibility writer", hardwareAccelerated: false));
        metrics.RecordWrite(TimeSpan.FromMilliseconds(2));

        metrics.End();
        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.FramesPerSecond);
        Assert.Equal(0, snapshot.QueueDepth);
        Assert.Equal(0, snapshot.ProcessingLatencyMilliseconds);
        Assert.Null(snapshot.EncoderName);
        Assert.False(snapshot.HardwareAccelerated);
    }

    [Fact]
    public async Task Backend_reports_empty_encoding_metrics_before_recording_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unpackvision-runtime-state-{Guid.NewGuid():N}");
        await using var backend = new MultiCameraRecordingBackend(
            new StorageOptions { RecordingRoot = root },
            new CameraRigOptions
            {
                Cameras =
                [
                    new CameraProfile
                    {
                        Id = "primary",
                        DisplayName = "Primary camera",
                        Enabled = true,
                        IsPrimary = true
                    }
                ]
            });

        var state = Assert.Single(backend.RuntimeStates);

        Assert.Equal(0, state.EncodingFramesPerSecond);
        Assert.Equal(0, state.EncodingQueueDepth);
        Assert.Equal(0, state.ProcessingLatencyMilliseconds);
        Assert.Null(state.EncoderName);
        Assert.False(state.HardwareAccelerated);
    }

    private sealed class StubFrameWriter(string encoderName, bool hardwareAccelerated) : IRecordingFrameWriter
    {
        public string Codec => "H.264";
        public string EncoderName { get; } = encoderName;
        public bool HardwareAccelerated { get; } = hardwareAccelerated;
        public void Write(Mat frame) { }
        public void Complete() { }
        public void Abort() { }
        public void Dispose() { }
    }
}
