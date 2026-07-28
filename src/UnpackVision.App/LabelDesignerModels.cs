using System.IO;
using System.Text.Json;

namespace UnpackVision.App;

public enum LabelElementKind
{
    Text,
    Code128,
    QrCode,
    Image,
    Line,
    Rectangle
}

public sealed class LabelElementModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LabelElementKind Kind { get; set; }
    public double X { get; set; } = 4;
    public double Y { get; set; } = 4;
    public double Width { get; set; } = 30;
    public double Height { get; set; } = 10;
    public double Rotation { get; set; }
    public string Content { get; set; } = "文本";
    public string DataField { get; set; } = string.Empty;
    public double FontSize { get; set; } = 12;
    public string Foreground { get; set; } = "#111111";
    public string Stroke { get; set; } = "#111111";
    public double StrokeThickness { get; set; } = 0.4;
    public string ImageBase64 { get; set; } = string.Empty;
    public bool ShowHumanReadable { get; set; } = true;
}

public sealed class LabelTemplateDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "未命名模板";
    public double WidthMm { get; set; } = 50;
    public double HeightMm { get; set; } = 30;
    public double OffsetXmm { get; set; }
    public double OffsetYmm { get; set; }
    public List<LabelElementModel> Elements { get; set; } = [];
}

public sealed class LabelTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LabelTemplateStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnpackVision", "Templates");
    }

    public string Root { get; }

    public IReadOnlyList<string> ListNames()
    {
        Directory.CreateDirectory(Root);
        return Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    public async Task SaveAsync(LabelTemplateDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Root);
        var safeName = string.Concat(document.Name.Trim().Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new ArgumentException("模板名称不能为空");
        }
        var path = Path.Combine(Root, safeName + ".json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
        File.Move(temporary, path, true);
    }

    public async Task<LabelTemplateDocument> LoadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Root, name + ".json");
        return await ReadAsync(path, cancellationToken);
    }

    public static async Task<LabelTemplateDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = JsonSerializer.Deserialize<LabelTemplateDocument>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("标签模板内容为空");
        if (document.Version != 1 || document.WidthMm is < 10 or > 300 || document.HeightMm is < 10 or > 300)
        {
            throw new InvalidDataException("标签模板版本或尺寸无效");
        }
        return document;
    }

    public static string Serialize(LabelTemplateDocument document) => JsonSerializer.Serialize(document, JsonOptions);
    public static LabelTemplateDocument Clone(LabelTemplateDocument document) =>
        JsonSerializer.Deserialize<LabelTemplateDocument>(Serialize(document), JsonOptions)!;
}
