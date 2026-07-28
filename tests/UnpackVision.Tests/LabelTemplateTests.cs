using UnpackVision.App;

namespace UnpackVision.Tests;

public sealed class LabelTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"UnpackVisionTemplates-{Guid.NewGuid():N}");

    [Fact]
    public async Task TemplateJsonRoundTripKeepsMillimetresAndEmbeddedImage()
    {
        var store = new LabelTemplateStore(_root);
        var document = new LabelTemplateDocument
        {
            Name = "退货异常标签",
            WidthMm = 60,
            HeightMm = 40,
            OffsetXmm = 0.8,
            Elements =
            [
                new LabelElementModel { Kind = LabelElementKind.Code128, Content = "UV-TAG-DAMAGE01", DataField = "条码" },
                new LabelElementModel { Kind = LabelElementKind.Image, ImageBase64 = Convert.ToBase64String([1, 2, 3, 4]) }
            ]
        };

        await store.SaveAsync(document);
        var loaded = await store.LoadAsync(document.Name);

        Assert.Equal(60, loaded.WidthMm);
        Assert.Equal(0.8, loaded.OffsetXmm);
        Assert.Equal("条码", loaded.Elements[0].DataField);
        Assert.Equal(document.Elements[1].ImageBase64, loaded.Elements[1].ImageBase64);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
