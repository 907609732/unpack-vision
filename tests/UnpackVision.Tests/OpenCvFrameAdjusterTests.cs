using OpenCvSharp;
using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class OpenCvFrameAdjusterTests
{
    [Fact]
    public void Default_controls_leave_frame_unchanged()
    {
        using var frame = new Mat(new Size(32, 24), MatType.CV_8UC3, new Scalar(40, 90, 160));
        using var original = frame.Clone();

        OpenCvFrameAdjuster.ApplyInPlace(frame, 50, 50, 50, 50);

        Assert.Equal(0, Cv2.Norm(frame, original, NormTypes.INF));
    }

    [Fact]
    public void Brightness_is_applied_in_software()
    {
        using var frame = new Mat(new Size(32, 24), MatType.CV_8UC3, Scalar.All(70));
        var before = Cv2.Mean(frame).Val0;

        OpenCvFrameAdjuster.ApplyInPlace(frame, 80, 50, 50, 50);

        Assert.True(Cv2.Mean(frame).Val0 > before + 40);
    }

    [Fact]
    public void Saturation_zero_produces_grayscale_pixels()
    {
        using var frame = new Mat(new Size(8, 8), MatType.CV_8UC3, new Scalar(15, 90, 210));

        OpenCvFrameAdjuster.ApplyInPlace(frame, 50, 50, 50, 0);

        var pixel = frame.At<Vec3b>(0, 0);
        Assert.InRange(Math.Abs(pixel.Item0 - pixel.Item1), 0, 1);
        Assert.InRange(Math.Abs(pixel.Item1 - pixel.Item2), 0, 1);
    }
}
