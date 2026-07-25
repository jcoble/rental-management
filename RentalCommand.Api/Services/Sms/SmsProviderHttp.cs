using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// Shared HTTP helpers for the <c>ISmsProvider</c> implementations: success-or-throw with the
/// provider's error body surfaced (so the outbox log shows *why* a send failed — bad number,
/// suspended account, …), and Basic-auth header construction.
/// </summary>
internal static class SmsProviderHttp
{
    public static AuthenticationHeaderValue BasicAuth(string user, string pass) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{pass}")));

    /// <summary>
    /// Throws with the provider's response body embedded when the response is non-success, so the
    /// outbox worker records a useful error and retries. No-op on success.
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = "(could not read response body)"; }

        throw new HttpRequestException(
            $"{provider} SMS send failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
    }

    /// <summary>
    /// Best-effort extraction for a provider receipt after the provider has already returned HTTP
    /// success. A malformed or changed success body must not turn an accepted send into a retry and
    /// risk sending the same text twice; the outbox can still retain its own stable delivery key.
    /// </summary>
    public static string? TryReadString(string body, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var current = document.RootElement;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(segment, out current))
                    return null;
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
