using System.Runtime.InteropServices;
using OpenCvSharp;
using UnpackVision.Core;
using ZXing;
using ZXing.Common;

namespace UnpackVision.Infrastructure;

public sealed class OpenCvImageScanService : IImageScanService
{
    public Task<ProcessedImageResult> ProcessAsync(
        ImageProcessRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Process(request, cancellationToken), cancellationToken);

    private static ProcessedImageResult Process(ImageProcessRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("输入图片不存在", request.InputPath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var source = Cv2.ImRead(request.InputPath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidDataException("无法读取输入图片");
        }

        using var rotated = Rotate(source, request.RotationDegrees);
        var (cropped, didCrop) = request.AutoCrop ? AutoCrop(rotated) : (rotated.Clone(), false);
        using (cropped)
        using (var enhanced = Enhance(cropped, request.Enhancement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
            if (!Cv2.ImWrite(request.OutputPath, enhanced))
            {
                throw new IOException("图片导出失败");
            }
            return new ProcessedImageResult(
                Path.GetFullPath(request.OutputPath),
                enhanced.Width,
                enhanced.Height,
                didCrop,
                request.Enhancement);
        }
    }

    private static Mat Rotate(Mat source, int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        var output = new Mat();
        switch (normalized)
        {
            case 0:
                source.CopyTo(output);
                break;
            case 90:
                Cv2.Rotate(source, output, RotateFlags.Rotate90Clockwise);
                break;
            case 180:
                Cv2.Rotate(source, output, RotateFlags.Rotate180);
                break;
            case 270:
                Cv2.Rotate(source, output, RotateFlags.Rotate90Counterclockwise);
                break;
            default:
                output.Dispose();
                throw new ArgumentOutOfRangeException(nameof(degrees), "旋转角度只能是 0、90、180 或 270");
        }
        return output;
    }

    private static (Mat Image, bool Cropped) AutoCrop(Mat source)
    {
        using var gray = new Mat();
        using var blurred = new Mat();
        using var edges = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
        Cv2.Canny(blurred, edges, 60, 180);
        Cv2.FindContours(
            edges,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var minimumArea = source.Width * source.Height * 0.15;
        foreach (var contour in contours.OrderByDescending(contour => Cv2.ContourArea(contour)))
        {
            if (Cv2.ContourArea(contour) < minimumArea)
            {
                break;
            }
            var perimeter = Cv2.ArcLength(contour, true);
            var polygon = Cv2.ApproxPolyDP(contour, perimeter * 0.02, true);
            if (polygon.Length != 4 || !Cv2.IsContourConvex(polygon))
            {
                continue;
            }
            return (PerspectiveCrop(source, polygon), true);
        }
        return (source.Clone(), false);
    }

    private static Mat PerspectiveCrop(Mat source, Point[] polygon)
    {
        var points = polygon.Select(point => new Point2f(point.X, point.Y)).ToArray();
        var topLeft = points.MinBy(point => point.X + point.Y);
        var bottomRight = points.MaxBy(point => point.X + point.Y);
        var topRight = points.MinBy(point => point.Y - point.X);
        var bottomLeft = points.MaxBy(point => point.Y - point.X);
        var width = (int)Math.Max(Distance(topLeft, topRight), Distance(bottomLeft, bottomRight));
        var height = (int)Math.Max(Distance(topLeft, bottomLeft), Distance(topRight, bottomRight));
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        Point2f[] destination =
        [
            new(0, 0),
            new(width - 1, 0),
            new(width - 1, height - 1),
            new(0, height - 1)
        ];
        Point2f[] ordered = [topLeft, topRight, bottomRight, bottomLeft];
        using var transform = Cv2.GetPerspectiveTransform(ordered, destination);
        var output = new Mat();
        Cv2.WarpPerspective(source, output, transform, new Size(width, height));
        return output;
    }

    private static double Distance(Point2f first, Point2f second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static Mat Enhance(Mat source, ImageEnhancementMode mode)
    {
        var output = new Mat();
        switch (mode)
        {
            case ImageEnhancementMode.Original:
                source.CopyTo(output);
                break;
            case ImageEnhancementMode.Color:
                Cv2.ConvertScaleAbs(source, output, 1.08, 5);
                break;
            case ImageEnhancementMode.Grayscale:
                Cv2.CvtColor(source, output, ColorConversionCodes.BGR2GRAY);
                break;
            case ImageEnhancementMode.BlackAndWhite:
                using (var gray = new Mat())
                {
                    Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.AdaptiveThreshold(
                        gray,
                        output,
                        255,
                        AdaptiveThresholdTypes.GaussianC,
                        ThresholdTypes.Binary,
                        31,
                        12);
                }
                break;
            default:
                output.Dispose();
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return output;
    }
}

public sealed class ZxingBarcodeRecognitionService : IBarcodeRecognitionService
{
    public Task<IReadOnlyList<BarcodeRecognitionResult>> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BarcodeRecognitionResult>>(() => Recognize(imagePath, cancellationToken), cancellationToken);

    private static IReadOnlyList<BarcodeRecognitionResult> Recognize(string imagePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("条码图片不存在", imagePath);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidDataException("无法读取条码图片");
        }
        using var continuous = image.IsContinuous() ? image.Clone() : image.Clone();
        var bytes = new byte[continuous.Rows * continuous.Cols * continuous.ElemSize()];
        Marshal.Copy(continuous.Data, bytes, 0, bytes.Length);
        var source = new RGBLuminanceSource(
            bytes,
            continuous.Width,
            continuous.Height,
            RGBLuminanceSource.BitmapFormat.BGR24);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                ReturnCodabarStartEnd = true
            }
        };
        var results = reader.DecodeMultiple(source) ?? [];
        return results.Select(result => new BarcodeRecognitionResult(
            result.Text,
            result.BarcodeFormat.ToString(),
            result.ResultPoints?.Select(point => ((double)point.X, (double)point.Y)).ToArray() ?? [])).ToArray();
    }
}

public sealed class OfflineOcrNotConfiguredService : IOfflineOcrService
{
    public bool IsAvailable => false;
    public string AvailabilityMessage => "尚未安装 PaddleOCR ONNX 离线模型包；系统不会回退到云端 OCR";
    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(AvailabilityMessage);
}
