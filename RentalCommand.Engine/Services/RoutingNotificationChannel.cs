using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Phase 4 <see cref="INotificationChannel"/>: routes SMS through the pluggable
/// <see cref="ISmsDispatcher"/> (per-portfolio BYO provider with platform-env fallback) and email
/// through a config-selectable transport — SMTP (e.g. Zoho) when <c>Notifications:Email:Transport</c>
/// is "Smtp" and SMTP creds are present, otherwise the SendGrid HTTP API. Both fall back to a
/// suppression log when nothing is configured so callers never need to guard on provider state.
/// Register via <c>AddHttpClient&lt;INotificationChannel, RoutingNotificationChannel&gt;()</c>
/// in Program.cs.
/// </summary>
public sealed class RoutingNotificationChannel : INotificationChannel
{
    private readonly HttpClient _http;
    private readonly NotificationsConfig _cfg;
    private readonly ISmsDispatcher _sms;
    private readonly ISmtpEmailSender _smtp;
    private readonly ILogger<RoutingNotificationChannel> _logger;

    public RoutingNotificationChannel(
        HttpClient http,
        IOptions<NotificationsConfig> options,
        ISmsDispatcher sms,
        ISmtpEmailSender smtp,
        ILogger<RoutingNotificationChannel> logger)
    {
        _http = http;
        _cfg  = options.Value;
        _sms = sms;
        _smtp = smtp;
        _logger = logger;
    }

    // ------------------------------------------------------------------ SMS (pluggable provider)

    // Delegates to the pluggable dispatcher: it resolves the portfolio's own provider (BYO creds)
    // first, then the platform-env fallback, and fail-soft logs when nothing is configured.
    public Task SendSmsAsync(string toPhoneNumber, string message, int? portfolioId = null, CancellationToken ct = default) =>
        _sms.SendAsync(portfolioId, toPhoneNumber, message, ct);

    // ------------------------------------------------------------------ Email (SMTP / SendGrid)

    public async Task SendEmailAsync(
        string toEmail, string subject, string body, string? htmlBody = null, CancellationToken ct = default)
    {
        // Transport selection (config-gated; no creds → no behaviour change vs the original SendGrid-only path):
        //   1. Transport == "Smtp" AND SMTP configured (host+user+pass) → send via SMTP (e.g. Zoho).
        //   2. else SendGrid configured                                 → send via SendGrid (unchanged).
        //   3. else                                                     → suppression log, never throw.
        // The upstream sandbox suppression lives in OutboxDispatchWorker and is intentionally untouched.
        var smtp = _cfg.Smtp;
        if (_cfg.Email.UseSmtp && smtp.Enabled)
        {
            await SendViaSmtpAsync(smtp, toEmail, subject, body, htmlBody, ct);
            return;
        }

        if (_cfg.SendGrid.Enabled)
        {
            await SendViaSendGridAsync(toEmail, subject, body, htmlBody, ct);
            return;
        }

        _logger.LogInformation(
            "[Email suppressed — not configured] to {To}: {Subject}",
            toEmail, subject);
    }

    // SMTP (e.g. Zoho): delegates the connect/auth/send to ISmtpEmailSender. Logs success/failure
    // consistently with the SendGrid path. The sender throws on failure → propagates so the outbox
    // worker retries (same contract as EnsureSuccessAsync below).
    private async Task SendViaSmtpAsync(
        SmtpOptions smtp, string toEmail, string subject, string body, string? htmlBody, CancellationToken ct)
    {
        try
        {
            await _smtp.SendAsync(smtp, toEmail, subject, body, htmlBody, ct);
        }
        catch (Exception ex)
        {
            // Surface why it failed (auth, bad host, rejected recipient, …) and re-throw for retry.
            throw new InvalidOperationException(
                $"SMTP send failed via {smtp.Host}:{smtp.Port}: {ex.Message}", ex);
        }

        _logger.LogInformation(
            "[Email sent via SMTP] To={To} Subject={Subject} Host={Host}",
            toEmail, subject, smtp.Host);
    }

    // SendGrid HTTP API. When an htmlBody is supplied we send BOTH a text/plain and a text/html
    // part (SendGrid requires text/plain to precede text/html in the content array); otherwise we
    // send text/plain only — unchanged from the original SendGrid-only behaviour.
    private async Task SendViaSendGridAsync(
        string toEmail, string subject, string body, string? htmlBody, CancellationToken ct)
    {
        var sg = _cfg.SendGrid;

        var content = string.IsNullOrWhiteSpace(htmlBody)
            ? new[] { new { type = "text/plain", value = body } }
            : new[]
            {
                new { type = "text/plain", value = body },
                new { type = "text/html", value = htmlBody }
            };

        var payload = new
        {
            personalizations = new[]
            {
                new { to = new[] { new { email = toEmail } } }
            },
            from = new { email = sg.FromEmail, name = sg.FromName },
            subject,
            content
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
