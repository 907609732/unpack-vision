using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using UnpackVision.Core;

namespace UnpackVision.StationHost;

public sealed class DesktopCommandBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:5272/"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<ScanAcknowledgement?> TryRouteAsync(
        ScanCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("device-command", command, JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ScanAcknowledgement>(JsonOptions, cancellationToken)
                : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    public async Task<StationStateSnapshot?> TryGetStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetFromJsonAsync<StationStateSnapshot>("state", JsonOptions, cancellationToken);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }
}

public sealed class StationHostOptions
{
    public string StationId { get; set; } = Environment.MachineName;
    public string AdvertisedAddress { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    public int PairingLifetimeMinutes { get; set; } = 5;
    public bool LanHttpPrototypeEnabled { get; set; }
}

public sealed class PairingSessionStore(IClock clock, StationHostOptions options)
{
    private readonly ConcurrentDictionary<Guid, PairingSessionDescriptor> _sessions = new();

    public PairingSessionDescriptor Create(Uri requestAddress)
    {
        CleanupExpired();
        var advertised = Uri.TryCreate(options.AdvertisedAddress, UriKind.Absolute, out var configured)
            ? configured
            : requestAddress;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var session = new PairingSessionDescriptor(
            Guid.NewGuid(),
            options.StationId,
            advertised,
            options.CertificateFingerprint,
            token,
            clock.Now.AddMinutes(Math.Clamp(options.PairingLifetimeMinutes, 1, 15)));
        _sessions[session.Id] = session;
        return session;
    }

    public bool TryConsume(Guid id, string token, out PairingSessionDescriptor? session)
    {
        session = null;
        if (!_sessions.TryGetValue(id, out var candidate))
        {
            return false;
        }
        if (candidate.ExpiresAt <= clock.Now)
        {
            _sessions.TryRemove(id, out _);
            return false;
        }
        var supplied = System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty);
        var expected = System.Text.Encoding.UTF8.GetBytes(candidate.Token);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            return false;
        }
        // Only a successfully authenticated request may consume the one-time
        // session. A wrong token must not invalidate the QR code for the phone.
        if (!_sessions.TryRemove(id, out var consumed) || consumed != candidate)
        {
            return false;
        }
        session = consumed;
        return true;
    }

    private void CleanupExpired()
    {
        foreach (var item in _sessions.Where(item => item.Value.ExpiresAt <= clock.Now))
        {
            _sessions.TryRemove(item.Key, out _);
        }
    }
}

public sealed record PairDeviceRequest(
    Guid SessionId,
    string Token,
    string Name,
    string PublicKey,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Scopes);

public sealed record MediaMtxAuthRequest(
    string User,
    string Password,
    string Token,
    string Ip,
    string Action,
    string Path,
    string Protocol);

public sealed record StationRecordImportRequest(
    string TrackingNo,
    string VideoPath,
    WorkflowMode Workflow = WorkflowMode.Unpacking,
    DateTimeOffset? ScannedAt = null,
    DateTimeOffset? RecordingStartedAt = null,
    DateTimeOffset? RecordingEndedAt = null);

public sealed class StationHub(IScanCommandRouter router) : Hub
{
    public Task<StationStateSnapshot> GetState() => Task.FromResult(router.GetState());

    public Task<ScanAcknowledgement> SubmitScan(
        ScanCommand command,
        CancellationToken cancellationToken) =>
        router.RouteAsync(command, cancellationToken);
}
