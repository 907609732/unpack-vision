using OpenCvSharp;

namespace UnpackVision.Infrastructure;

public static class OpenCvFrameAdjuster
{
    public static void ApplyInPlace(
        Mat frame,
        double brightness,
        double contrast,
        double sharpness,
        double saturation)
    {
        if (frame.Empty())
        {
            return;
        }

        brightness = Math.Clamp(brightness, 0, 100);
        contrast = Math.Clamp(contrast, 0, 100);
        sharpness = Math.Clamp(sharpness, 0, 100);
        saturation = Math.Clamp(saturation, 0, 100);

        var contrastScale = 0.5 + contrast / 100d;
        var brightnessOffset = (brightness - 50d) * 2d;
        if (Math.Abs(contrastScale - 1d) > 0.005 || Math.Abs(brightnessOffset) > 0.5)
        {
            frame.ConvertTo(frame, -1, contrastScale, brightnessOffset);
        }

        if (frame.Channels() == 3 && Math.Abs(saturation - 50d) > 0.5)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);
            var channels = Cv2.Split(hsv);
            try
            {
                channels[1].ConvertTo(channels[1], -1, saturation / 50d);
                Cv2.Merge(channels, hsv);
                Cv2.CvtColor(hsv, frame, ColorConversionCodes.HSV2BGR);
            }
            finally
            {
                foreach (var channel in channels)
                {
                    channel.Dispose();
                }
            }
        }

        if (Math.Abs(sharpness - 50d) <= 0.5)
        {
            return;
        }

        using var blurred = new Mat();
        var sigma = sharpness < 50
            ? 0.8 + (50d - sharpness) / 18d
            : 1.2;
        Cv2.GaussianBlur(frame, blurred, new Size(0, 0), sigma);
        if (sharpness < 50)
        {
            var originalWeight = sharpness / 50d;
            Cv2.AddWeighted(frame, originalWeight, blurred, 1d - originalWeight, 0, frame);
            return;
        }

        var amount = (sharpness - 50d) / 32d;
        Cv2.AddWeighted(frame, 1d + amount, blurred, -amount, 0, frame);
    }
}
