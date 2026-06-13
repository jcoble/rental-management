using System.Text.Json;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// Vonage (formerly Nexmo) SMS via its REST API. Credential slots: A = api_key, B = api_secret.
/// Messages POST a <c>api_key/api_secret/from/to/text</c> form to <c>https://rest.nexmo.com/sms/json</c>.
/// Vonage returns HTTP 200 even for logical failures, signalling the real result in each message's
/// <c>status</c> field ("0" == delivered-to-carrier), so this provider inspects the body, not just the
/// HTTP code. See https://developer.vonage.com/en/api/sms.
/// </summary>
public sealed class VonageSmsProvider : ISmsProvider
{
    private readonly HttpClient _http;

    public VonageSmsProvider(HttpClient http) => _http = http;

    public SmsProviderKey Key => SmsProviderKey.Vonage;

    public async Task SendAsync(SmsCredentials credentials, string toPhoneNumber, string message, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://rest.nexmo.com/sms/json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api_key"] = credentials.CredentialA!,
                ["api_secret"] = credentials.CredentialB!,
                ["from"] = credentials.FromNumber!,
                ["to"] = toPhoneNumber,
                ["text"] = message,
            }),
        };

        var response = await _http.SendAsync(request, ct);
        await SmsProviderHttp.EnsureSuccessAsync(response, "Vonage", ct);

        // Vonage signals logical failure in the body even on HTTP 200 — surface it as a throw so the
        // outbox worker retries and the error is recorded.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (doc.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
            {
                var status = m.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (status is not null && status != "0")
                {
                    var error = m.TryGetProperty("error-text", out var e) ? e.GetString() : "(no error text)";
                    throw new HttpRequestException($"Vonage SMS send failed: status {status} — {error}");
                }
            }
        }
    }
}
