namespace UnpackVision.Core;

public interface IScanCommandRouter
{
    Task<ScanAcknowledgement> RouteAsync(
        ScanCommand command,
        CancellationToken cancellationToken = default);

    StationStateSnapshot GetState();
}

public interface IScanCommandLedger
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<ScanAcknowledgement?> GetAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);
    Task SaveAsync(
        string idempotencyKey,
        ScanAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}

public interface IPairedDeviceRegistry
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PairedDevice>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DevicePairingCredential> PairAsync(
        DeviceRegistration registration,
        CancellationToken cancellationToken = default);
    Task<PairedDevice?> AuthenticateAsync(
        string deviceId,
        string accessToken,
        string requiredScope,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(
        string deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(
        string deviceId,
        CancellationToken cancellationToken = default);
}

public interface IMediaRelayManager : IAsyncDisposable
{
    bool IsRunning { get; }
    Task<MediaRelayStatus> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task DisconnectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    MediaPublishEndpoint CreatePublishEndpoint(string host, string deviceId);
    MediaLiveEndpoint CreateLiveEndpoint(string host, string deviceId, string authUser);
}
