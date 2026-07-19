using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace UnpackVision.Infrastructure;

public static class BarcodePresentationService
{
    public static byte[] CreateCode128Png(string value, int width = 620, int height = 150)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = width,
                Height = height,
                Margin = 8,
                PureBarcode = true
            }
        };
        var pixels = writer.Write(value);
        using var bgra = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC4, pixels.Pixels);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.ImEncode(".png", bgr, out var encoded);
        return encoded;
    }
}

public static class VideoPresentationService
{
    public static byte[]? CreateThumbnailJpeg(string? videoPath, int width = 360)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return null;
        }
        try
        {
            using var capture = new VideoCapture(videoPath);
            if (!capture.IsOpened())
            {
                return null;
            }
            var frameCount = capture.Get(VideoCaptureProperties.FrameCount);
            if (frameCount > 10)
            {
                capture.Set(VideoCaptureProperties.PosFrames, Math.Min(frameCount - 1, frameCount * 0.12));
            }
            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
            {
                return null;
            }
            var targetHeight = Math.Max(1, (int)Math.Round(frame.Height * (double)width / frame.Width));
            using var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(width, targetHeight), interpolation: InterpolationFlags.Area);
            Cv2.ImEncode(
                ".jpg",
                resized,
                out var encoded,
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]);
            return encoded;
        }
        catch (OpenCVException)
        {
            return null;
        }
    }
}
