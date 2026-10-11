using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// Telnyx SMS via its v2 REST Messaging API. Credential slots: A = API key (bearer). Messages POST a
/// JSON body of <c>{from,to,text}</c> to <c>https://api.telnyx.com/v2/messages</c>.
/// See https://developers.telnyx.com/api/messaging/send-message.
/// </summary>
public sealed class TelnyxSmsProvider : ISmsProvider
{
    private readonly HttpClient _http;

    public TelnyxSmsProvider(HttpClient http) => _http = http;

    public SmsProviderKey Key => SmsProviderKey.Telnyx;

    public async Task<SmsProviderReceipt> SendAsync(
        SmsCredentials credentials,
        string toPhoneNumber,
        string message,
        NotificationDeliveryContext delivery,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            from = credentials.FromNumber,
            to = toPhoneNumber,
            text = message,
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.telnyx.com/v2/messages")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.CredentialA);

        var response = await _http.SendAsync(request, ct);
        await SmsProviderHttp.EnsureSuccessAsync(response, "Telnyx", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new SmsProviderReceipt(SmsProviderHttp.TryReadString(body, "data", "id"));
    }
}
