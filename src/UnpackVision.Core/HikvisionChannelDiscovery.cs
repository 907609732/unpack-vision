namespace UnpackVision.Core;

/// <summary>
/// Supplies the connection details needed for a one-shot recorder channel discovery.
/// The password is deliberately transient and must never be persisted or logged by an adapter.
/// </summary>
public sealed record HikvisionDiscoveryRequest(
    string Host,
    int HttpPort,
    string Username,
    string Password);

public sealed record HikvisionStreamDescriptor(
    int StreamId,
    string Codec,
    int Width,
    int Height,
    double FramesPerSecond);

public sealed record HikvisionChannelDescriptor(
    int Channel,
    HikvisionStreamDescriptor? MainStream,
    HikvisionStreamDescriptor? SubStream)
{
    public bool HasAnyStream => MainStream is not null || SubStream is not null;
}

public interface IHikvisionChannelDiscoveryService
{
    Task<IReadOnlyList<HikvisionChannelDescriptor>> DiscoverAsync(
        HikvisionDiscoveryRequest request,
        CancellationToken cancellationToken = default);
}
