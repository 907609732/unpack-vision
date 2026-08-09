using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } =
        Environment.GetEnvironmentVariable("UNPACKVISION_TELEMETRY_ENDPOINT") ?? string.Empty;
    public string Platform { get; set; } = "windows";
    public string AppVersion { get; set; } = "unknown";
    public string Channel { get; set; } = "stable";
    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "Telemetry");
}

public sealed class CloudflareUsageTelemetry(
    HttpClient httpClient,
    TelemetryOptions options) : IUsageTelemetry
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task TrackAsync(
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled ||
            !string.Equals(eventName, "app.daily_active", StringComparison.Ordinal) ||
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var day = BeijingNow().ToString("yyyy-MM-dd");
            Directory.CreateDirectory(options.StateDirectory);
            var statePath = Path.Combine(options.StateDirectory, "last-sent.txt");
            if (File.Exists(statePath) &&
                string.Equals((await File.ReadAllTextAsync(statePath, cancellationToken)).Trim(), day, StringComparison.Ordinal))
            {
                return;
            }

            var secret = await GetOrCreateSecretAsync(cancellationToken);
            string dailyId;
            try
            {
                dailyId = Convert.ToHexString(HMACSHA256.HashData(
                    secret,
                    Encoding.UTF8.GetBytes($"dau:v1|{day}|{options.Platform}")));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await httpClient.PostAsJsonAsync(endpoint, new
            {
                day,
                dailyId,
                platform = options.Platform,
                appVersion = options.AppVersion,
                channel = options.Channel
            }, requestCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var temporary = statePath + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, day, cancellationToken);
            File.Move(temporary, statePath, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            // Anonymous telemetry must never interrupt startup or business work.
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteLocalIdentityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (Directory.Exists(options.StateDirectory))
            {
                Directory.Delete(options.StateDirectory, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Withdrawal must stay best-effort and must never interrupt startup.
        }
        return Task.CompletedTask;
    }

    private async Task<byte[]> GetOrCreateSecretAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.StateDirectory, "installation-key.protected");
        if (File.Exists(path))
        {
            return ProtectedData.Unprotect(
                await File.ReadAllBytesAsync(path, cancellationToken),
                null,
                DataProtectionScope.CurrentUser);
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, protectedSecret, cancellationToken);
        File.Move(temporary, path, true);
        return secret;
    }

    private static DateTimeOffset BeijingNow()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
    }
}
