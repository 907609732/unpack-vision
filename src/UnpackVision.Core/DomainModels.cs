using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnpackVision.Core;

public enum WorkflowMode
{
    Unpacking,
    Packing,
    ScanCollection
}

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Saving,
    Completed,
    Collected,
    Failed,
    Imported
}

public enum SyncStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed
}

public sealed record ScannerProfile
{
    public string? ScannerDeviceId { get; init; }
    public string Terminator { get; init; } = "Enter";
    public string TrackingPattern { get; init; } = "^[A-Za-z0-9-]+$";
    public int MinimumLength { get; init; } = 10;
    public int MaximumLength { get; init; } = 30;
    public bool CaseSensitive { get; init; }
    public int DebounceMilliseconds { get; init; } = 1000;
    public bool FilterPrefixEnabled { get; init; }
    public string PrefixToRemove { get; init; } = "";
    public bool FilterSuffixEnabled { get; init; }
    public string SuffixToRemove { get; init; } = "";

    public string Normalize(string? raw) => (raw ?? string.Empty).Trim('\r', '\n', '\t', ' ');

    public string PrepareTrackingNumber(string? raw)
    {
        var value = Normalize(raw);
        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (FilterPrefixEnabled && !string.IsNullOrEmpty(PrefixToRemove) && value.StartsWith(PrefixToRemove, comparison))
        {
            value = value[PrefixToRemove.Length..];
        }
        if (FilterSuffixEnabled && !string.IsNullOrEmpty(SuffixToRemove) && value.EndsWith(SuffixToRemove, comparison))
        {
            value = value[..^SuffixToRemove.Length];
        }
        return value;
    }

    public string? ValidateTrackingNumber(string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length < MinimumLength || normalized.Length > MaximumLength)
        {
            return $"单号长度必须在 {MinimumLength} 到 {MaximumLength} 位之间";
        }

        return Regex.IsMatch(normalized, TrackingPattern, RegexOptions.CultureInvariant)
            ? null
            : "单号格式不符合当前规则";
    }
}

public sealed class ScanRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TrackingNo { get; set; } = string.Empty;
    public WorkflowMode Workflow { get; set; } = WorkflowMode.Unpacking;
    public RecordingState State { get; set; } = RecordingState.Starting;
    public DateTimeOffset ScannedAt { get; set; }
    public DateTimeOffset? RecordingStartedAt { get; set; }
    public DateTimeOffset? RecordingEndedAt { get; set; }
    public string? VideoPath { get; set; }
    public IReadOnlyList<string> Snapshots { get; set; } = [];
    public string? CameraId { get; set; }
    public string StationId { get; set; } = Environment.MachineName;
    public Guid? DuplicateOf { get; set; }
    public string PlatformMatchStatus { get; set; } = "待匹配";
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? NoteUpdatedAt { get; set; }
    public IReadOnlyList<RecordTagAssignment> Tags { get; set; } = [];
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string SnapshotsJson
    {
        get => JsonSerializer.Serialize(Snapshots);
        set => Snapshots = string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<string[]>(value) ?? [];
    }
}

public sealed record IssueTagDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public string Name { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#FF9500";
    public string BarcodeValue { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class RecordTagAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecordId { get; set; }
    public string TagId { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#FF9500";
    public DateTimeOffset TaggedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string Source { get; set; } = "scanner";
    public bool IsActive => RemovedAt is null;
}

public static class IssueTagDefaults
{
    public const string UndoBarcode = "UV-UNDO-TAG";

    public static List<IssueTagDefinition> Create() =>
    [
        new()
        {
            Id = "DAMAGE01",
            Name = "破损",
            ColorHex = "#FF3B30",
            BarcodeValue = "UV-TAG-DAMAGE01",
            SortOrder = 0
        },
        new()
        {
            Id = "SWAPPED1",
            Name = "调包",
            ColorHex = "#AF52DE",
            BarcodeValue = "UV-TAG-SWAPPED1",
            SortOrder = 1
        }
    ];
}

public sealed class SyncDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecordId { get; set; }
    public string ConnectorId { get; set; } = string.Empty;
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int AttemptCount { get; set; }
    public string? ExternalId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextRetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record RecordingSession(
    Guid RecordId,
    string TrackingNo,
    WorkflowMode Workflow,
    DateTimeOffset StartedAt,
    string TemporaryPath);

public sealed record RecordingCompletion(string VideoPath, DateTimeOffset EndedAt);

public sealed record ParsedRecording(
    string TrackingNo,
    WorkflowMode Workflow,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string VideoPath);
