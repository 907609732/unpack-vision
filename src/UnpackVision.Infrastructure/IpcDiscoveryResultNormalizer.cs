using System.Net;
using System.Net.Sockets;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

/// <summary>
/// Treats every WS-Discovery response as hostile input. Only same-host private-network
/// HTTP(S) service addresses survive this boundary; the UI never receives arbitrary URLs.
/// </summary>
internal static class IpcDiscoveryResultNormalizer
{
    internal static IReadOnlyList<IpcDiscoveryDevice> Normalize(
        IEnumerable<IpcDiscoveryDevice> devices,
        int maximumDevices = 64,
        int maximumAddressesPerDevice = 8)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (maximumDevices is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        }
        if (maximumAddressesPerDevice is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAddressesPerDevice));
        }

        var normalized = new Dictionary<string, IpcDiscoveryDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in devices.Take(maximumDevices * 4))
        {
            if (!TryNormalizeRemoteAddress(candidate.RemoteAddress, out var remoteAddress))
            {
                continue;
            }

            var addresses = candidate.Addresses
                .Take(maximumAddressesPerDevice * 4)
                .Select(address => NormalizeServiceAddress(address, remoteAddress))
                .Where(address => address is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maximumAddressesPerDevice)
                .ToArray();
            if (addresses.Length == 0)
            {
                continue;
            }

            var endpointReference = SafeText(candidate.EndpointReference, 256);
            var key = string.IsNullOrWhiteSpace(endpointReference)
                ? $"{remoteAddress}|{addresses[0]}"
                : $"{endpointReference}|{remoteAddress}";
            var next = new IpcDiscoveryDevice(
                endpointReference,
                SafeText(candidate.DisplayName, 96),
                SafeText(candidate.Manufacturer, 96),
                SafeText(candidate.Model, 128),
                remoteAddress.ToString(),
                addresses,
                candidate.Scopes
                    .Take(32)
                    .Select(scope => SafeText(scope, 512))
                    .Where(scope => !string.IsNullOrWhiteSpace(scope))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
            {
                Source = candidate.Source,
                Sources = NormalizeSources(candidate)
            };

            if (normalized.TryGetValue(key, out var existing))
            {
                next = existing with
                {
                    DisplayName = Prefer(existing.DisplayName, next.DisplayName),
                    Manufacturer = Prefer(existing.Manufacturer, next.Manufacturer),
                    Model = Prefer(existing.Model, next.Model),
                    Addresses = existing.Addresses.Concat(next.Addresses)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(maximumAddressesPerDevice)
                        .ToArray(),
                    Scopes = existing.Scopes.Concat(next.Scopes)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(32)
                        .ToArray(),
                    Source = existing.Source == IpcDiscoverySource.HikvisionSadp ||
                             next.Source == IpcDiscoverySource.HikvisionSadp
                        ? IpcDiscoverySource.HikvisionSadp
                        : existing.Source,
                    Sources = existing.Sources
                        .Append(existing.Source)
                        .Concat(next.Sources)
                        .Append(next.Source)
                        .Where(Enum.IsDefined)
                        .Distinct()
                        .OrderBy(source => source)
                        .ToArray()
                };
            }
            normalized[key] = next;
            if (normalized.Count >= maximumDevices)
            {
                break;
            }
        }

        return normalized.Values
            .OrderBy(device => ParseAddress(device.RemoteAddress), IpAddressComparer.Instance)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string Prefer(string current, string candidate) =>
        string.IsNullOrWhiteSpace(current) ? candidate : current;

    private static IReadOnlyList<IpcDiscoverySource> NormalizeSources(IpcDiscoveryDevice candidate) =>
        candidate.Sources
            .Append(candidate.Source)
            .Where(Enum.IsDefined)
            .Distinct()
            .OrderBy(source => source)
            .Take(Enum.GetValues<IpcDiscoverySource>().Length)
            .ToArray();

    private static string SafeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            return string.Empty;
        }
        return value.Trim();
    }

    private static string? NormalizeServiceAddress(string? value, IPAddress remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(uri.Host, out var advertisedAddress) ||
            !advertisedAddress.Equals(remoteAddress))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Host = advertisedAddress.ToString(),
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static bool TryNormalizeRemoteAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl) ||
            !IPAddress.TryParse(value.Trim(), out var parsed) || !IsPrivateUnicast(parsed))
        {
            return false;
        }
        address = parsed;
        return true;
    }

    private static bool IsPrivateUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast || address.IsIPv6LinkLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6UniqueLocal;
    }

    private static IPAddress ParseAddress(string value) => IPAddress.Parse(value);

    private sealed class IpAddressComparer : IComparer<IPAddress>
    {
        internal static IpAddressComparer Instance { get; } = new();

        public int Compare(IPAddress? left, IPAddress? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var family = left.AddressFamily.CompareTo(right.AddressFamily);
            if (family != 0) return family;
            var leftBytes = left.GetAddressBytes();
            var rightBytes = right.GetAddressBytes();
            for (var index = 0; index < Math.Min(leftBytes.Length, rightBytes.Length); index++)
            {
                var result = leftBytes[index].CompareTo(rightBytes[index]);
                if (result != 0) return result;
            }
            return leftBytes.Length.CompareTo(rightBytes.Length);
        }
    }
}
