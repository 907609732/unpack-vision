using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnpackVision.Core;

namespace UnpackVision.Infrastructure;

public sealed class WebhookEventPublisher(HttpClient httpClient, WebhookOptions options) : IEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(string eventType, ScanRecord record, CancellationToken cancellationToken = default)
    {
        if (options.Endpoints.Count == 0) return;
        var timestamp = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.Serialize(new
        {
            eventId,
            idempotencyKey = eventId,
            eventType,
            timestamp,
            record
        }, JsonOptions);
        var signature = Sign(timestamp, body, options.Secret);
        foreach (var endpoint in options.Endpoints.Where(value => Uri.TryCreate(value, UriKind.Absolute, out _)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-UnpackVision-Event", eventType);
            request.Headers.Add("X-UnpackVision-Event-Id", eventId);
            request.Headers.Add("X-UnpackVision-Timestamp", timestamp.ToUnixTimeSeconds().ToString());
            request.Headers.Add("X-UnpackVision-Signature", $"sha256={signature}");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    internal static string Sign(DateTimeOffset timestamp, string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp.ToUnixTimeSeconds()}.{body}"))).ToLowerInvariant();
    }
}
