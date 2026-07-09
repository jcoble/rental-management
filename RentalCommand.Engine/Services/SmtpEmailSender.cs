using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Sends an email over SMTP (authenticated mailbox SMTP or IP-authorized relay). Pulled out of
/// <see cref="RoutingNotificationChannel"/> so the MailKit dependency and the connect/auth/send
/// dance live in one focused, testable place. The channel decides WHICH transport to use; this
/// class only knows HOW to talk SMTP. Throws on failure so the outbox worker can retry — mirroring
/// the SendGrid path's exception-on-failure contract.
/// </summary>
public interface ISmtpEmailSender
{
    Task SendAsync(SmtpOptions smtp, string toEmail, string subject, string body, string? htmlBody = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SmtpEmailSender : ISmtpEmailSender
{
    public async Task SendAsync(
        SmtpOptions smtp, string toEmail, string subject, string body, string? htmlBody = null, CancellationToken ct = default)
    {
        // Caller (RoutingNotificationChannel) only reaches here when smtp.Enabled, so Host is present
        // and either credentials exist or FromEmail is available for no-auth relay.
        var fromEmail = string.IsNullOrWhiteSpace(smtp.FromEmail) ? smtp.Username! : smtp.FromEmail!;
        var fromName = string.IsNullOrWhiteSpace(smtp.FromName) ? "Rental Command" : smtp.FromName!;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;

        // The outbox carries a plaintext body (the source of truth / fallback) plus an optional
        // pre-composed htmlBody. Use the supplied HTML verbatim when present (e.g. auth emails with a
        // "Here" hyperlink); otherwise wrap a minimal HTML alternative around the plaintext so clients
        // still render a clean HTML part.
        var bodyBuilder = new BodyBuilder
        {
            TextBody = body,
            HtmlBody = string.IsNullOrWhiteSpace(htmlBody) ? BuildHtmlBody(body) : htmlBody,
        };
        message.Body = bodyBuilder.ToMessageBody();

        // Port 465 = SSL on connect; 587 = STARTTLS. UseSsl drives the choice but the
        // port stays authoritative so an explicit 587 still upgrades via STARTTLS even if UseSsl is true.
        var socketOptions = smtp.Port == 587
            ? SecureSocketOptions.StartTls
            : smtp.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

        using var client = new SmtpClient
        {
            // Bound every SMTP op so a dead/slow relay cannot stall the single outbox dispatcher.
            Timeout = Math.Max(1, smtp.TimeoutSeconds) * 1000,
        };

        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, socketOptions, ct);
            if (smtp.HasCredentials)
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);
            }
            await client.SendAsync(message, ct);
        }
        finally
        {
            // Best-effort quit even if send threw, so the connection is closed cleanly.
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, ct); } catch { /* ignore */ }
            }
        }
    }

    // Minimal, safe HTML alternative: escape the plaintext and turn newlines into <br>. The outbox
    // body is plain text, so there is no HTML to preserve — just present it cleanly to HTML clients.
    private static string BuildHtmlBody(string body)
    {
        var encoded = WebUtility.HtmlEncode(body ?? string.Empty).Replace("\n", "<br>\n");
        return $"<!DOCTYPE html><html><body style=\"font-family:-apple-system,Segoe UI,Roboto,sans-serif;" +
               $"line-height:1.6;color:#1a1a2e;\"><p>{encoded}</p></body></html>";
    }
}
