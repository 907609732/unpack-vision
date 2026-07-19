using System.Runtime.InteropServices;
using OpenCvSharp;
using UnpackVision.Core;
using UnpackVision.Infrastructure;
using ZXing;
using ZXing.QrCode;

namespace UnpackVision.Tests;

public sealed class ImageServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionImages-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProcessesDocumentLocallyAndExportsGrayscale()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "document.png");
        var output = Path.Combine(_root, "processed.png");
        using (var image = new Mat(new Size(800, 600), MatType.CV_8UC3, Scalar.All(35)))
        {
            Cv2.Rectangle(image, new Rect(80, 60, 640, 480), Scalar.All(245), -1);
            Cv2.PutText(image, "RETURN 00123", new Point(150, 300), HersheyFonts.HersheySimplex, 1.4, Scalar.All(20), 3);
            Cv2.ImWrite(input, image);
        }

        var result = await new OpenCvImageScanService().ProcessAsync(new ImageProcessRequest(
            input,
            output,
            AutoCrop: true,
            RotationDegrees: 0,
            Enhancement: ImageEnhancementMode.Grayscale));

        Assert.True(File.Exists(result.OutputPath));
        Assert.True(result.Cropped);
        using var processed = Cv2.ImRead(output, ImreadModes.Unchanged);
        Assert.Equal(1, processed.Channels());
        Assert.InRange(processed.Width, 600, 650);
    }

    [Fact]
    public async Task RecognizesQrCodeWithoutNetwork()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "qr.png");
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 320,
                Height = 320,
                Margin = 2
            }
        };
        var pixels = writer.Write("STOP-UNPACK-2026");
        using (var image = new Mat(pixels.Height, pixels.Width, MatType.CV_8UC4))
        {
            Marshal.Copy(pixels.Pixels, 0, image.Data, pixels.Pixels.Length);
            Cv2.ImWrite(input, image);
        }

        var results = await new ZxingBarcodeRecognitionService().RecognizeAsync(input);

        Assert.Contains(results, result => result.Text == "STOP-UNPACK-2026" && result.Format == "QR_CODE");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
        GC.SuppressFinalize(this);
    }
}
