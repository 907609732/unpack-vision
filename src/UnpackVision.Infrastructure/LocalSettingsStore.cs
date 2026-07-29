using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class LocalSettings
{
    public ScannerProfile Scanner { get; set; } = new();
    public WorkflowMode Workflow { get; set; } = WorkflowMode.Unpacking;
    public int MaximumRecordingMinutes { get; set; } = 30;
    public CameraOptions Camera { get; set; } = new();
    public string RecordingRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "UnpackVision");
    public string ExcelWorkbookPath { get; set; } = string.Empty;
    public bool ShowLivePreview { get; set; } = true;
    public bool VoiceEnabled { get; set; } = true;
    public int VoiceVolume { get; set; } = 100;
    public bool FaceZoomEnabled { get; set; }
    public bool CaptureSnapshotOnIssueTag { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public ConsentState Consent { get; set; } = new();
    public DonationProfile Donation { get; set; } = new();
    public List<IssueTagDefinition> IssueTags { get; set; } = IssueTagDefaults.Create();
}

public sealed class LocalSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LocalSettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnpackVision",
            "settings.json");
    }

    public string Path { get; }

    public async Task<LocalSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new LocalSettings();
        }
        await using var stream = File.OpenRead(Path);
        var settings = await JsonSerializer.DeserializeAsync<LocalSettings>(stream, SerializerOptions, cancellationToken)
            ?? new LocalSettings();
        if (settings.IssueTags is null || settings.IssueTags.Count == 0)
        {
            settings.IssueTags = IssueTagDefaults.Create();
        }
        settings.Consent ??= new ConsentState();
        settings.Donation ??= new DonationProfile();
        if (settings.Scanner.MinimumLength == 6 &&
            settings.Scanner.MaximumLength == 40 &&
            settings.Scanner.DebounceMilliseconds == 80)
        {
            settings.Scanner = settings.Scanner with
            {
                MinimumLength = 10,
                MaximumLength = 30,
                DebounceMilliseconds = 1000
            };
        }
        return settings;
    }

    public async Task SaveAsync(LocalSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, Path, true);
    }
}
