namespace UnpackVision.Core;

public sealed class SetupState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; }
    public Guid WorkspaceId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool ExcelSkipped { get; set; }

    public bool IsComplete =>
        Version >= CurrentVersion &&
        WorkspaceId != Guid.Empty &&
        CompletedAt is not null;
}

public sealed class TelemetryConsentState
{
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? ChangedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}

public sealed class WorkspaceManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid WorkspaceId { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class PortableScanRecord
{
    public int SchemaVersion { get; set; } = WorkspaceManifest.CurrentSchemaVersion;
    public Guid Id { get; set; }
    public string TrackingNo { get; set; } = string.Empty;
    public WorkflowMode Workflow { get; set; }
    public RecordingState State { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public DateTimeOffset? RecordingStartedAt { get; set; }
    public DateTimeOffset? RecordingEndedAt { get; set; }
    public string? RelativeVideoPath { get; set; }
    public IReadOnlyList<string> RelativeSnapshots { get; set; } = [];
    public string? CameraId { get; set; }
    public string StationId { get; set; } = string.Empty;
    public Guid? DuplicateOf { get; set; }
    public string PlatformMatchStatus { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? NoteUpdatedAt { get; set; }
    public IReadOnlyList<RecordTagAssignment> Tags { get; set; } = [];
    public string? FailureReason { get; set; }
    public string? ExcelSyncStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum RecoveryItemKind
{
    Complete,
    FileNameOnly,
    ExcelMatched,
    MissingVideo,
    Conflict,
    Invalid
}

public sealed record RecoveryItem(
    RecoveryItemKind Kind,
    string Description,
    PortableScanRecord? Record = null,
    string? SourcePath = null);

public sealed class RecoveryPreview
{
    public Guid WorkspaceId { get; set; }
    public IReadOnlyList<RecoveryItem> Items { get; set; } = [];
    public int CompleteCount => Items.Count(item => item.Kind == RecoveryItemKind.Complete);
    public int FileNameOnlyCount => Items.Count(item => item.Kind == RecoveryItemKind.FileNameOnly);
    public int ExcelMatchedCount => Items.Count(item => item.Kind == RecoveryItemKind.ExcelMatched);
    public int MissingVideoCount => Items.Count(item => item.Kind == RecoveryItemKind.MissingVideo);
    public int ConflictCount => Items.Count(item => item.Kind == RecoveryItemKind.Conflict);
    public int InvalidCount => Items.Count(item => item.Kind == RecoveryItemKind.Invalid);
}

public sealed record RecoveryConflict(
    Guid RecordId,
    string Field,
    string Resolution,
    DateTimeOffset ResolvedAt);

public sealed record RecoveryResult(
    int Added,
    int Updated,
    int Skipped,
    string ReportPath,
    IReadOnlyList<RecoveryConflict> Conflicts);

public sealed record WorkbookValidationResult(
    bool Valid,
    string Message,
    string WorksheetName = "退货扫码单号");
