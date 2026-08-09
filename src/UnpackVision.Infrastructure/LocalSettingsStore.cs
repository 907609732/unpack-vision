using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class LocalSettings
{
    public const int CurrentRecordingTimeoutDefaultVersion = 1;

    public ScannerProfile Scanner { get; set; } = new();
    public WorkflowMode Workflow { get; set; } = WorkflowMode.Unpacking;
    public int MaximumRecordingMinutes { get; set; } = 5;
    // Zero identifies JSON created before this migration; Normalize upgrades it once.
    public int RecordingTimeoutDefaultVersion { get; set; }
    public CameraOptions Camera { get; set; } = new();
    public CameraRigOptions CameraRig { get; set; } = new();
    public CameraPreviewLayoutSettings PreviewLayout { get; set; } = new();
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
    public SetupState Setup { get; set; } = new();
    public TelemetryConsentState Telemetry { get; set; } = new();
    public ConsentState Consent { get; set; } = new();
    public DonationProfile Donation { get; set; } = new();
    public int IssueTagCatalogVersion { get; set; }
    public List<IssueTagDefinition> IssueTags { get; set; } = IssueTagDefaults.Create();
}

/// <summary>
/// Persists only the operator's monitoring-wall arrangement. Camera ownership and recording
/// remain in <see cref="CameraRigOptions"/>, so rearranging preview slots never restarts a
/// capture pipeline or changes which media assets are recorded.
/// </summary>
public sealed class CameraPreviewLayoutSettings
{
    public int ViewCount { get; set; }
    public List<string> CameraIds { get; set; } = [];
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
        LocalSettings settings;
        var hasSavedSettings = File.Exists(Path);
        var persistLegacyRecordingTimeoutMigration = false;
        if (!hasSavedSettings)
        {
            settings = new LocalSettings();
        }
        else
        {
            await using var stream = File.OpenRead(Path);
            settings = await JsonSerializer.DeserializeAsync<LocalSettings>(stream, SerializerOptions, cancellationToken)
                ?? new LocalSettings();
        }

        persistLegacyRecordingTimeoutMigration = hasSavedSettings &&
            settings.RecordingTimeoutDefaultVersion < LocalSettings.CurrentRecordingTimeoutDefaultVersion &&
            settings.MaximumRecordingMinutes == 30;
        Normalize(settings);
        if (persistLegacyRecordingTimeoutMigration)
        {
            await SaveAsync(settings, cancellationToken);
        }
        return settings;
    }

    public async Task SaveAsync(LocalSettings settings, CancellationToken cancellationToken = default)
    {
        Normalize(settings);
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

    private static void Normalize(LocalSettings settings)
    {
        // Version the default separately so a later user choice of 30 minutes is never
        // mistaken for the legacy default during subsequent launches.
        if (settings.RecordingTimeoutDefaultVersion < LocalSettings.CurrentRecordingTimeoutDefaultVersion)
        {
            if (settings.MaximumRecordingMinutes == 30)
            {
                settings.MaximumRecordingMinutes = 5;
            }
            settings.RecordingTimeoutDefaultVersion = LocalSettings.CurrentRecordingTimeoutDefaultVersion;
        }
        settings.MaximumRecordingMinutes = Math.Clamp(settings.MaximumRecordingMinutes, 1, 1440);
        settings.Camera ??= new CameraOptions();
        settings.CameraRig ??= new CameraRigOptions();
        NormalizeCameraRig(settings);
        NormalizePreviewLayout(settings);
        settings.IssueTags ??= [];
        if (settings.IssueTagCatalogVersion < IssueTagDefaults.CurrentCatalogVersion)
        {
            var additions = settings.IssueTags.Count == 0
                ? IssueTagDefaults.Create()
                : IssueTagDefaults.Create()
                    .Where(tag => tag.Id is IssueTagDefaults.MissingTagId or IssueTagDefaults.PurchaseTagId)
                    .ToList();
            var nextSortOrder = settings.IssueTags.Count == 0
                ? 0
                : settings.IssueTags.Max(tag => tag.SortOrder) + 1;
            foreach (var definition in additions)
            {
                if (settings.IssueTags.Any(tag =>
                        string.Equals(tag.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                settings.IssueTags.Add(definition with { SortOrder = nextSortOrder++ });
            }
            settings.IssueTagCatalogVersion = IssueTagDefaults.CurrentCatalogVersion;
        }
        settings.Consent ??= new ConsentState();
        settings.Donation ??= new DonationProfile();
        if (!settings.Donation.IsConfigured)
        {
            settings.Donation = new DonationProfile();
        }
        settings.Setup ??= new SetupState();
        settings.Telemetry ??= new TelemetryConsentState();
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
    }

    private static void NormalizeCameraRig(LocalSettings settings)
    {
        settings.CameraRig.Cameras ??= [];
        if (settings.CameraRig.Cameras.Count == 0)
        {
            settings.CameraRig.Cameras.Add(new CameraProfile
            {
                DisplayName = "主机位",
                IsPrimary = true,
                SortOrder = 0,
                SourceType = (CameraSourceType)settings.Camera.SourceKind,
                WindowsSymbolicLink = settings.Camera.WindowsSymbolicLink,
                LegacyCameraIndex = settings.Camera.CameraIndex,
                AutoSelectBestCamera = settings.Camera.AutoSelectBestCamera,
                Width = settings.Camera.Width,
                Height = settings.Camera.Height,
                FramesPerSecond = settings.Camera.FramesPerSecond,
                Codec = settings.Camera.Codec,
                Brightness = settings.Camera.Brightness,
                Contrast = settings.Camera.Contrast,
                Sharpness = settings.Camera.Sharpness,
                Saturation = settings.Camera.Saturation,
                AutoFocus = settings.Camera.AutoFocus,
                NetworkStreamUrl = settings.Camera.NetworkStreamUrl,
                NetworkUsername = settings.Camera.NetworkUsername,
                NetworkPasswordProtected = settings.Camera.NetworkPasswordProtected,
                HikvisionHost = settings.Camera.HikvisionHost,
                HikvisionHttpPort = settings.Camera.HikvisionHttpPort,
                HikvisionRtspPort = settings.Camera.HikvisionRtspPort,
                HikvisionChannel = settings.Camera.HikvisionChannel,
                HikvisionSubStream = settings.Camera.HikvisionSubStream
            });
        }

        if (settings.CameraRig.SchemaVersion < CameraRigOptions.CurrentSchemaVersion)
        {
            // Older profiles used every enabled camera. Preserve existing multi-camera
            // behavior during migration while keeping ordinary single-camera setups simple.
            settings.CameraRig.Mode = settings.CameraRig.Cameras.Count(camera => camera.Enabled) > 1
                ? CameraRigMode.MultiCamera
                : CameraRigMode.SingleCamera;
            settings.CameraRig.SchemaVersion = CameraRigOptions.CurrentSchemaVersion;
        }

        var enabled = settings.CameraRig.Cameras
            .Where(camera => camera.Enabled)
            .OrderBy(camera => camera.SortOrder)
            .Take(CameraRigOptions.MaximumEnabledCameras)
            .ToArray();
        foreach (var extra in settings.CameraRig.Cameras.Where(camera => camera.Enabled).Except(enabled))
        {
            extra.Enabled = false;
        }
        var primary = enabled.FirstOrDefault(camera => camera.IsPrimary) ?? enabled.FirstOrDefault();
        foreach (var camera in settings.CameraRig.Cameras)
        {
            camera.Id = string.IsNullOrWhiteSpace(camera.Id) ? Guid.NewGuid().ToString("N") : camera.Id.Trim();
            camera.DisplayName = string.IsNullOrWhiteSpace(camera.DisplayName) ? "机位" : camera.DisplayName.Trim();
            camera.IsPrimary = ReferenceEquals(camera, primary);
            camera.FramesPerSecond = camera.FramesPerSecond <= 0 ? 15 : camera.FramesPerSecond;
            camera.HikvisionHttpPort = camera.HikvisionHttpPort is < 1 or > 65535 ? 80 : camera.HikvisionHttpPort;
        }
        if (primary is null)
        {
            return;
        }

        settings.Camera.SourceKind = (CameraSourceKind)primary.SourceType;
        settings.Camera.WindowsSymbolicLink = primary.WindowsSymbolicLink;
        settings.Camera.CameraIndex = primary.LegacyCameraIndex;
        settings.Camera.AutoSelectBestCamera = primary.AutoSelectBestCamera;
        settings.Camera.Width = primary.Width;
        settings.Camera.Height = primary.Height;
        settings.Camera.FramesPerSecond = primary.FramesPerSecond;
        settings.Camera.Codec = primary.Codec;
        settings.Camera.Brightness = primary.Brightness;
        settings.Camera.Contrast = primary.Contrast;
        settings.Camera.Sharpness = primary.Sharpness;
        settings.Camera.Saturation = primary.Saturation;
        settings.Camera.AutoFocus = primary.AutoFocus;
        settings.Camera.NetworkStreamUrl = primary.NetworkStreamUrl;
        settings.Camera.NetworkUsername = primary.NetworkUsername;
        settings.Camera.NetworkPasswordProtected = primary.NetworkPasswordProtected;
        settings.Camera.HikvisionHost = primary.HikvisionHost;
        settings.Camera.HikvisionHttpPort = primary.HikvisionHttpPort;
        settings.Camera.HikvisionRtspPort = primary.HikvisionRtspPort;
        settings.Camera.HikvisionChannel = primary.HikvisionChannel;
        settings.Camera.HikvisionSubStream = primary.HikvisionSubStream;
    }

    private static void NormalizePreviewLayout(LocalSettings settings)
    {
        settings.PreviewLayout ??= new CameraPreviewLayoutSettings();
        var cameras = settings.CameraRig.EnabledCameras
            .Take(CameraRigOptions.MaximumEnabledCameras)
            .ToArray();
        var viewCount = settings.PreviewLayout.ViewCount <= 0
            ? Math.Max(1, cameras.Length)
            : Math.Clamp(settings.PreviewLayout.ViewCount, 1, CameraRigOptions.MaximumEnabledCameras);
        var validIds = cameras.Select(camera => camera.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(viewCount);
        foreach (var id in settings.PreviewLayout.CameraIds ?? [])
        {
            var trimmed = id?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                normalized.Add(string.Empty);
            }
            else if (validIds.Contains(trimmed) && !normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(trimmed);
            }
            if (normalized.Count == viewCount)
            {
                break;
            }
        }
        foreach (var camera in cameras.Where(camera =>
                     !normalized.Contains(camera.Id, StringComparer.OrdinalIgnoreCase)))
        {
            var emptyIndex = normalized.FindIndex(string.IsNullOrEmpty);
            if (emptyIndex >= 0)
            {
                normalized[emptyIndex] = camera.Id;
            }
            else if (normalized.Count < viewCount)
            {
                normalized.Add(camera.Id);
            }
        }
        while (normalized.Count < viewCount)
        {
            normalized.Add(string.Empty);
        }
        settings.PreviewLayout.ViewCount = viewCount;
        settings.PreviewLayout.CameraIds = normalized;
    }
}
