namespace UnpackVision.Core;

public interface IWorkbookTemplateService
{
    Task CreateAsync(
        string path,
        string worksheetName = "退货扫码单号",
        CancellationToken cancellationToken = default);

    Task<WorkbookValidationResult> ValidateAsync(
        string path,
        string worksheetName = "退货扫码单号",
        CancellationToken cancellationToken = default);
}

public interface IPortableRecordCatalog
{
    string RecordingRoot { get; }
    Task<WorkspaceManifest> EnsureWorkspaceAsync(
        Guid? preferredWorkspaceId = null,
        CancellationToken cancellationToken = default);
    Task WriteAsync(
        ScanRecord record,
        SyncDelivery? delivery = null,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid recordId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecoveryItem>> ReadAllAsync(CancellationToken cancellationToken = default);
}

public interface IWorkspaceRecoveryService
{
    Task<RecoveryPreview> PreviewAsync(
        string recordingRoot,
        string? workbookPath,
        CancellationToken cancellationToken = default);

    Task<RecoveryResult> RecoverAsync(
        RecoveryPreview preview,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Copies and verifies the application-owned recording archive before the caller
/// atomically switches its settings. Source cleanup is a separate, explicitly invoked
/// phase so a failed settings save can never make a completed recording unavailable.
/// </summary>
public interface IRecordingRootMigrationService
{
    Task<RecordingRootMigrationPreview> PreviewAsync(
        string sourceRoot,
        string targetRoot,
        CancellationToken cancellationToken = default);

    Task<RecordingRootMigrationResult> MigrateAsync(
        RecordingRootMigrationPreview preview,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task VerifyAsync(
        RecordingRootMigrationResult migration,
        CancellationToken cancellationToken = default);

    Task<RecordingRootMigrationCleanupResult> CleanupSourceAsync(
        RecordingRootMigrationResult migration,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RecordingRootMigrationCleanupResult> RetryCleanupAsync(
        RecordingRootMigrationResult migration,
        IProgress<RecordingRootMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record RecordingRootMigrationPreview(
    string SourceRoot,
    string TargetRoot,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> RelativePaths)
{
    public bool HasContent => FileCount > 0;
}

public sealed record RecordingRootMigrationProgress(
    int CompletedFiles,
    int TotalFiles,
    string RelativePath,
    long CompletedBytes,
    long TotalBytes,
    long CurrentFileBytes,
    long CurrentFileTotalBytes);

public sealed record RecordingRootMigrationResult(
    int CopiedFiles,
    int VerifiedExistingFiles,
    long CopiedBytes,
    string ReportPath,
    string SourceRoot,
    string TargetRoot,
    IReadOnlyList<RecordingRootMigrationFileReceipt> VerifiedFiles);

public sealed record RecordingRootMigrationFileReceipt(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record RecordingRootMigrationCleanupFailure(
    string RelativePath,
    string Reason);

public sealed record RecordingRootMigrationCleanupResult(
    int DeletedFiles,
    long DeletedBytes,
    IReadOnlyList<RecordingRootMigrationCleanupFailure> RemainingFiles,
    string ReportPath)
{
    public bool IsComplete => RemainingFiles.Count == 0;
}
