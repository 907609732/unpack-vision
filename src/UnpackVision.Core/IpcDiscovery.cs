namespace UnpackVision.Core;

/// <summary>
/// Defines one bounded, user-initiated local IPC discovery operation. Discovery never carries
/// camera credentials and only returns unverified local-network advertisements.
/// </summary>
public sealed record IpcDiscoveryRequest(
    TimeSpan Timeout,
    int MaximumDevices = 64)
{
    /// <summary>
    /// Gets the bounded default used when a caller supplies <see cref="TimeSpan.Zero"/>.
    /// Zero is the public "use the product default" value; it never means an unbounded scan.
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the timeout every adapter must apply after resolving the public zero-value.
    /// </summary>
    public TimeSpan EffectiveTimeout => Timeout == TimeSpan.Zero ? DefaultTimeout : Timeout;
}

/// <summary>
/// Identifies the discovery mechanism that produced an IPC candidate. The value is
/// informational only; every returned address must pass the same infrastructure safety
/// boundary regardless of its source.
/// </summary>
public enum IpcDiscoverySource
{
    OnvifWsDiscovery = 0,
    CompatibilityProbe = 1,
    HikvisionSadp = 2
}

/// <summary>
/// A normalized IPC advertisement. Addresses remain strings at the port boundary so the
/// infrastructure security filter can reject malformed input before constructing a URI.
/// </summary>
public sealed record IpcDiscoveryDevice(
    string EndpointReference,
    string DisplayName,
    string Manufacturer,
    string Model,
    string RemoteAddress,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Scopes)
{
    private IReadOnlyList<IpcDiscoverySource>? _sources;

    /// <summary>
    /// Gets the non-secret discovery mechanism that reported this device. Keeping this as
    /// an init-only property preserves the original positional constructor for callers.
    /// </summary>
    public IpcDiscoverySource Source { get; init; } = IpcDiscoverySource.OnvifWsDiscovery;

    /// <summary>
    /// Gets every discovery mechanism that contributed to this candidate. <see cref="Source"/>
    /// remains the legacy primary value for existing JSON/wire consumers; older producers that
    /// omit this additive field are interpreted as reporting only <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<IpcDiscoverySource> Sources
    {
        get => _sources is { Count: > 0 } ? _sources : [Source];
        init => _sources = value is { Count: > 0 } ? value : null;
    }
}

/// <summary>
/// Contains normalized candidates plus bounded, non-sensitive operational notices that a
/// host can show to the operator without exposing raw network responses.
/// </summary>
public sealed record IpcDiscoveryScanResult(
    IReadOnlyList<IpcDiscoveryDevice> Devices,
    IReadOnlyList<string> Notices);

/// <summary>
/// Discovers unverified IPC candidates on the local multicast domain.
/// </summary>
public interface IIpcDiscoveryService
{
    Task<IpcDiscoveryScanResult> DiscoverAsync(
        IpcDiscoveryRequest request,
        CancellationToken cancellationToken = default);
}
