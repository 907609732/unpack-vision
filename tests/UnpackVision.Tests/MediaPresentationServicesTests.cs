using UnpackVision.Infrastructure;

namespace UnpackVision.Tests;

public sealed class MediaPresentationServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionMedia-{Guid.NewGuid():N}");

    [Fact]
    public async Task GeneratedCode128BarcodeCanBeReadLocally()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stop.png");
        await File.WriteAllBytesAsync(path, BarcodePresentationService.CreateCode128Png("STOP-RECORD-2026"));
        var recognition = new ZxingBarcodeRecognitionService();

        var results = await recognition.RecognizeAsync(path);

        Assert.Contains(results, result => result.Text == "STOP-RECORD-2026");
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
