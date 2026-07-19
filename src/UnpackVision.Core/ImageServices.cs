namespace UnpackVision.Core;

public enum ImageEnhancementMode
{
    Original,
    Color,
    Grayscale,
    BlackAndWhite
}

public sealed record ImageProcessRequest(
    string InputPath,
    string OutputPath,
    bool AutoCrop = true,
    int RotationDegrees = 0,
    ImageEnhancementMode Enhancement = ImageEnhancementMode.Color);

public sealed record ProcessedImageResult(
    string OutputPath,
    int Width,
    int Height,
    bool Cropped,
    ImageEnhancementMode Enhancement);

public sealed record BarcodeRecognitionResult(
    string Text,
    string Format,
    IReadOnlyList<(double X, double Y)> Points);

public sealed record OcrTextBlock(
    string Text,
    double Confidence,
    IReadOnlyList<(double X, double Y)> Polygon);

public sealed record OcrResult(IReadOnlyList<OcrTextBlock> Blocks, string FullText);

public interface IImageScanService
{
    Task<ProcessedImageResult> ProcessAsync(ImageProcessRequest request, CancellationToken cancellationToken = default);
}

public interface IBarcodeRecognitionService
{
    Task<IReadOnlyList<BarcodeRecognitionResult>> RecognizeAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
}

public interface IOfflineOcrService
{
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default);
}
