using System.Net.Http.Headers;
using System.Text;

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
}
