using Makaretu.Dns;

namespace UnpackVision.StationHost;

/// <summary>
/// Advertises the station with the standard DNS-SD/mDNS protocol so paired
/// phones can recover after Wi-Fi, hotspot or USB-tether addresses change.
/// </summary>
public sealed class StationDiscoveryPublisher(
    StationHostOptions options,
    ILogger<StationDiscoveryPublisher> logger) : IHostedService, IDisposable
{
    public const string ServiceType = "_unpackvision._tcp";
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.LanHttpsEnabled)
        {
            return Task.CompletedTask;
        }

        try
        {
            _profile = new ServiceProfile(
                $"UnpackVision-{options.StationId}",
                ServiceType,
                checked((ushort)options.LanHttpsPort));
            _profile.AddProperty("stationId", options.StationId);
            _profile.AddProperty("version", "3");
            _profile.AddProperty("tls", "1");

            _discovery = new ServiceDiscovery();
            _discovery.Advertise(_profile);
            logger.LogInformation(
                "Station discovery advertised as {ServiceType} on port {Port}.",
                ServiceType,
                options.LanHttpsPort);
        }
        catch (Exception exception)
        {
            // Discovery is a recovery aid. A blocked multicast adapter must not
            // prevent the HTTP station service from starting.
            logger.LogWarning(exception, "Unable to advertise the station with mDNS.");
            Dispose();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _discovery?.Dispose();
        _discovery = null;
        _profile = null;
    }
}
