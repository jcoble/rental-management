using System.Net.Http.Headers;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// SignalWire SMS via its Twilio-compatible LaML/Compatibility API. Credential slots:
/// A = ProjectId (Basic-auth user), B = API Token (Basic-auth pass), C = Space URL. Messages POST a
/// <c>From/To/Body</c> form to <c>https://{Space}/api/laml/2010-04-01/Accounts/{ProjectId}/Messages.json</c>.
/// </summary>
public sealed class SignalWireSmsProvider : ISmsProvider
{
    private readonly HttpClient _http;

    public SignalWireSmsProvider(HttpClient http) => _http = http;

    public SmsProviderKey Key => SmsProviderKey.SignalWire;

    public async Task<SmsProviderReceipt> SendAsync(
        SmsCredentials credentials,
        string toPhoneNumber,
        string message,
        NotificationDeliveryContext delivery,
        CancellationToken ct = default)
    {
        var projectId = credentials.CredentialA!;
        var token = credentials.CredentialB!;
        var space = (credentials.CredentialC ?? string.Empty).Trim().TrimEnd('/');
        if (!space.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            space = "https://" + space;

        var url = $"{space}/api/laml/2010-04-01/Accounts/{projectId}/Messages.json";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = credentials.FromNumber!,
                ["To"] = toPhoneNumber,
                ["Body"] = message,
            }),
        };
        request.Headers.Authorization = SmsProviderHttp.BasicAuth(projectId, token);

        var response = await _http.SendAsync(request, ct);
        await SmsProviderHttp.EnsureSuccessAsync(response, "SignalWire", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new SmsProviderReceipt(SmsProviderHttp.TryReadString(body, "sid"));
    }
}
