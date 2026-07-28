namespace UnpackVision.Core;

public enum DeviceOperatingMode
{
    SmartCamera,
    HandheldScanner,
    ScanCollection,
    IssueRemote
}

public enum DeviceThermalState
{
    Unknown,
    Nominal,
    Fair,
    Serious,
    Critical
}

public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<string> Roles { get; set; } = [];
    public IReadOnlyList<string> Scopes { get; set; } = [];
    public string PublicKey { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int? BatteryPercent { get; set; }
    public DeviceThermalState ThermalState { get; set; }
    public string NetworkQuality { get; set; } = "unknown";
    public IReadOnlyDictionary<string, string> Capabilities { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsRevoked => RevokedAt is not null;
}

public sealed record ScanCommand
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string DeviceId { get; init; } = string.Empty;
    public string StationId { get; init; } = Environment.MachineName;
    public string Value { get; init; } = string.Empty;
    public string Format { get; init; } = "unknown";
    public DeviceOperatingMode Mode { get; init; } = DeviceOperatingMode.HandheldScanner;
    public WorkflowMode Workflow { get; init; } = WorkflowMode.Unpacking;
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
    public string IdempotencyKey { get; init; } = string.Empty;

    public string EffectiveIdempotencyKey => string.IsNullOrWhiteSpace(IdempotencyKey)
        ? EventId.ToString("N")
        : IdempotencyKey.Trim();
}

public enum ScanCommandAction
{
    RecordingStarted,
    RecordingStopped,
    RecordingSwitched,
    Collected,
    IssueTagged,
    IssueUndone,
    NoteUpdated,
    SnapshotCaptured,
    Ignored,
    Rejected,
    Failed
}

public static class IssueRemoteCommands
{
    public const string Stop = "UV-STOP";
    public const string Snapshot = "UV-SNAPSHOT";
    public const string NotePrefix = "UV-NOTE:";
}

public sealed record ScanAcknowledgement(
    Guid EventId,
    ScanCommandAction Action,
    RecordingState State,
    Guid? RecordId,
    string? TrackingNo,
    string Message,
    DateTimeOffset ServerTime);

public sealed class MediaGap
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecordId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public bool Recovered { get; set; }
    public string? RecoveryPath { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed record StationStateSnapshot(
    string StationId,
    RecordingState RecordingState,
    Guid? RecordId,
    string? TrackingNo,
    DateTimeOffset ServerTime,
    bool MediaRelayRunning = false);

public sealed record PairingSessionDescriptor(
    Guid Id,
    string StationId,
    Uri StationAddress,
    string CertificateFingerprint,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record DeviceRegistration(
    string Name,
    string PublicKey,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes);

public sealed record DevicePairingCredential(
    PairedDevice Device,
    string AccessToken);

public sealed record MediaRelayStatus(
    bool Running,
    string Version,
    string? LastError = null);

public sealed record MediaPublishEndpoint(
    string StreamPath,
    Uri RtspUrl,
    string AuthUser);

public sealed record MediaLiveEndpoint(
    string StreamPath,
    Uri WhepUrl,
    string AuthUser);

public sealed record StationRecordView(
    Guid Id,
    string TrackingNo,
    WorkflowMode Workflow,
    RecordingState State,
    DateTimeOffset ScannedAt,
    DateTimeOffset? RecordingStartedAt,
    DateTimeOffset? RecordingEndedAt,
    double? DurationSeconds,
    string Note,
    IReadOnlyList<RecordTagAssignment> Tags,
    string? CameraId,
    string StationId,
    Guid? DuplicateOf,
    string PlatformMatchStatus,
    string? FailureReason,
    bool HasVideo,
    long? VideoBytes,
    bool HasThumbnail,
    DateTimeOffset UpdatedAt);

public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor);

public sealed record StationRecordEvent(
    string Type,
    DateTimeOffset At,
    string Message,
    string? TagId = null,
    Guid? AssignmentId = null);
