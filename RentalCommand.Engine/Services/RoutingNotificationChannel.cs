using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Phase 4 <see cref="INotificationChannel"/>: routes SMS through Twilio and email
/// through SendGrid when the respective provider is configured. Falls back to a
/// suppression log when unconfigured so callers never need to guard on provider state.
/// Register via <c>AddHttpClient&lt;INotificationChannel, RoutingNotificationChannel&gt;()</c>
/// in Program.cs.
/// </summary>
public sealed class RoutingNotificationChannel : INotificationChannel
{
    private readonly HttpClient _http;
    private readonly NotificationsConfig _cfg;
    private readonly ILogger<RoutingNotificationChannel> _logger;

    public RoutingNotificationChannel(
        HttpClient http,
        IOptions<NotificationsConfig> options,
        ILogger<RoutingNotificationChannel> logger)
    {
        _http = http;
        _cfg  = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------ SMS (Twilio)

    public async Task SendSmsAsync(string toPhoneNumber, string message, CancellationToken ct = default)
    {
        var twilio = _cfg.Twilio;

        if (!twilio.Enabled)
        {
            _logger.LogInformation(
                "[SMS suppressed — Twilio not configured] to {To}: {Message}",
                toPhoneNumber, message);
            return;
        }

        var url = $"https://api.twilio.com/2010-04-01/Accounts/{twilio.AccountSid}/Messages.json";

        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{twilio.AccountSid}:{twilio.AuthToken}"));

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = twilio.FromNumber!,
                ["To"]   = toPhoneNumber,
                ["Body"] = message,
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "Twilio", ct);

        _logger.LogInformation(
            "[SMS sent via Twilio] To={To} Status={Status}",
            toPhoneNumber, (int)response.StatusCode);
    }

    // ------------------------------------------------------------------ Email (SendGrid)

    public async Task SendEmailAsync(
        string toEmail, string subject, string body, CancellationToken ct = default)
    {
        var sg = _cfg.SendGrid;

        if (!sg.Enabled)
        {
            _logger.LogInformation(
                "[Email suppressed — SendGrid not configured] to {To}: {Subject}",
                toEmail, subject);
            return;
        }

        var payload = new
        {
            personalizations = new[]
            {
                new { to = new[] { new { email = toEmail } } }
            },
            from = new { email = sg.FromEmail, name = sg.FromName },
            subject,
            content = new[]
            {
                new { type = "text/plain", value = body }
            }
        };

        var json    = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sg.ApiKey);

        var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "SendGrid", ct);

        _logger.LogInformation(
            "[Email sent via SendGrid] To={To} Subject={Subject} Status={Status}",
            toEmail, subject, (int)response.StatusCode);
    }

    // On a non-success response, surface the provider's error body in the thrown exception so the
    // outbox log shows *why* it failed (invalid number, rejected address, suspended account, …)
    // instead of a bare status code. The exception still propagates so OutboxDispatchWorker retries.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = "(could not read response body)"; }

        throw new HttpRequestException(
            $"{provider} send failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
    }
}
