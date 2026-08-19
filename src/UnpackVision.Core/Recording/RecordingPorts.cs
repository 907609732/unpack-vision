namespace UnpackVision.Core;

/// <summary>
/// Boundary between recording workflows and the camera/encoder implementation.
/// Implementations own capture-thread and native-resource lifetime.
/// </summary>
public interface IRecordingBackend : IAsyncDisposable
{
    Task<RecordingSession> StartAsync(
        Guid recordId,
        string trackingNo,
        WorkflowMode workflow,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken);

    Task<RecordingCompletion> StopAsync(
        RecordingSession session,
        CancellationToken cancellationToken);

    Task AbortAsync(RecordingSession session, CancellationToken cancellationToken);

    Task UpdateIssueOverlayAsync(
        Guid recordId,
        IReadOnlyList<RecordTagAssignment> activeTags,
        CancellationToken cancellationToken = default);

    Task<string> TakeSnapshotAsync(CancellationToken cancellationToken = default);
}
