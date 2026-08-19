using OpenCvSharp;
using UnpackVision.Core;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class RecordingFrameWriterTests
{
    [Fact]
    public void Pipe_write_timeout_terminates_encoder_and_returns_within_hard_limit()
    {
        using var stream = new NeverCompletingWriteStream();
        var terminated = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<TimeoutException>(() =>
            GStreamerRecordingFrameWriter.WriteWithHardTimeout(
                stream,
                new byte[16],
                TimeSpan.FromMilliseconds(80),
                () => terminated = true));

        Assert.True(terminated);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 60, 1000);
        Assert.DoesNotContain(Path.GetTempPath(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HardwareWriterPipelineUsesBoundedNonLeakyQueueAndNv12()
    {
        var encoder = new MediaEncoderCapability(
            MediaEncoderFamily.NvidiaNvenc,
            "nvh264enc",
            true,
            true,
            true);

        var arguments = GStreamerRecordingFrameWriter.BuildArguments(
            encoder,
            Path.Combine(Path.GetTempPath(), "writer test.mp4"),
            15,
            new Size(1920, 1080));

        Assert.Contains("fd=0", arguments);
        Assert.Contains("blocksize=6220800", arguments);
        Assert.Contains("format=bgr", arguments);
        Assert.Contains("framerate=15/1", arguments);
        Assert.Contains("video/x-raw,format=NV12", arguments);
        Assert.Contains("max-size-buffers=3", arguments);
        Assert.Contains("max-size-bytes=0", arguments);
        Assert.Contains("max-size-time=0", arguments);
        Assert.DoesNotContain("leaky=downstream", arguments);
        Assert.Contains("h264parse", arguments);
        Assert.Contains("mp4mux", arguments);
        Assert.Contains("faststart=true", arguments);
        Assert.Contains("fragment-duration=1000", arguments);
        Assert.Contains(
            arguments,
            argument => argument.StartsWith("location=", StringComparison.Ordinal) &&
                        !argument.Contains('\\'));
    }

    [Fact]
    public void ApprovedSoftwareWriterPipelineUsesI420()
    {
        var encoder = new MediaEncoderCapability(
            MediaEncoderFamily.Software,
            "openh264enc",
            true,
            false,
            true);

        var arguments = GStreamerRecordingFrameWriter.BuildArguments(
            encoder,
            "recording.mp4",
            10,
            new Size(1280, 720));

        Assert.Contains("video/x-raw,format=I420", arguments);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(1919, 1080)]
    [InlineData(1920, 1079)]
    public void WriterPipelineRejectsInvalidH264Dimensions(int width, int height)
    {
        var encoder = new MediaEncoderCapability(
            MediaEncoderFamily.NvidiaNvenc,
            "nvh264enc",
            true,
            true,
            true);

        Assert.ThrowsAny<ArgumentException>(() =>
            GStreamerRecordingFrameWriter.BuildArguments(
                encoder,
                "recording.mp4",
                15,
                new Size(width, height)));
    }

    [Fact]
    public async Task GStreamerWriterSmokeCanBeEnabledOnQualifiedWindowsHost()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("UNPACKVISION_GSTREAMER_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var capabilities = await new GStreamerRuntimeProbe().ProbeAsync();
        var selection = new GStreamerH264EncoderSelector().Select(
            capabilities,
            requireHardwareAcceleration: true,
            requireBundledRedistributionApproval: true);
        Assert.True(selection.Success, selection.Message);
        Assert.NotNull(selection.Encoder);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.LaunchExecutablePath));

        var directory = Path.Combine(
            Path.GetTempPath(),
            "UnpackVision GStreamer Smoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "frames.partial.mp4");
        try
        {
            using (var writer = new GStreamerRecordingFrameWriter(
                       capabilities.LaunchExecutablePath!,
                       selection.Encoder!,
                       path,
                       15,
                       new Size(320, 240)))
            {
                for (var index = 0; index < 30; index++)
                {
                    using var frame = new Mat(
                        new Size(320, 240),
                        MatType.CV_8UC3,
                        new Scalar(index * 7 % 255, 80, 160));
                    writer.Write(frame);
                }
                writer.Complete();
            }

            Assert.True(File.Exists(path), $"GStreamer did not create {path}");
            Assert.True(new FileInfo(path).Length > 1024);
            using var capture = new VideoCapture(path);
            Assert.True(capture.IsOpened());
            Assert.Equal(320, (int)capture.FrameWidth);
            Assert.Equal(240, (int)capture.FrameHeight);
            Assert.InRange(capture.FrameCount, 28, 30);
            Assert.InRange(capture.Fps, 14.5, 15.5);
        }
        finally
        {
            try
            {
                if (!string.Equals(
                        Environment.GetEnvironmentVariable("UNPACKVISION_GSTREAMER_SMOKE_KEEP"),
                        "1",
                        StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Delayed antivirus or decoder handles must not hide the media assertion.
            }
        }
    }

    private sealed class NeverCompletingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
    }
}
