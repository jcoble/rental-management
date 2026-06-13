using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// Twilio SMS via its LaML/Compatibility API. Credential slots: A = AccountSid (Basic-auth user),
/// B = AuthToken (Basic-auth pass). Messages POST a <c>From/To/Body</c> form to
/// <c>https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json</c>.
/// </summary>
public sealed class TwilioSmsProvider : ISmsProvider
{
    private readonly HttpClient _http;

    public TwilioSmsProvider(HttpClient http) => _http = http;

    public SmsProviderKey Key => SmsProviderKey.Twilio;

    public async Task SendAsync(SmsCredentials credentials, string toPhoneNumber, string message, CancellationToken ct = default)
    {
        var accountSid = credentials.CredentialA!;
        var authToken = credentials.CredentialB!;
        var url = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = credentials.FromNumber!,
                ["To"] = toPhoneNumber,
                ["Body"] = message,
            }),
        };
        request.Headers.Authorization = SmsProviderHttp.BasicAuth(accountSid, authToken);

        var response = await _http.SendAsync(request, ct);
        await SmsProviderHttp.EnsureSuccessAsync(response, "Twilio", ct);
    }
}
