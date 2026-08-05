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
