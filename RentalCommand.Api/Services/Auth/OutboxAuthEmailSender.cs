using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Enqueues email-confirmation and password-reset emails via the DB outbox.
/// The Engine's <c>OutboxDispatchWorker</c> dispatches them through SendGrid (or logs
/// a suppressed send when SendGrid is not configured).
/// </summary>
public sealed class OutboxAuthEmailSender : IAuthEmailSender
{
    private static readonly AtomicJsonResultCodec<AuthEmailOutboxResult> ResultCodec =
        new("auth-email-outbox-result:v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxAuthEmailSender> _logger;

    public OutboxAuthEmailSender(
        IAtomicUnitOfWork atomic,
        IConfiguration configuration,
        ILogger<OutboxAuthEmailSender> logger)
    {
        _atomic = atomic;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailConfirmationAsync(
        ApplicationUser user,
        int? expectedPortfolioId,
        string token,
        string operationKey,
        CancellationToken ct = default)
    {
        var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
        var link = $"{webBase}/verify-email?userId={user.Id}&token={Uri.EscapeDataString(token)}";

        var greeting = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : user.Email ?? "there";
        var subject = "Confirm your Rental Command email";

        // Plaintext fallback keeps the raw URL so a text-only client can still complete the flow.
        var body = $"""
Hi {greeting},

Thanks for signing up for Rental Command! Please confirm your email — click the link below:

{link}

This link will expire within 24 hours. If you didn't create an account, you can safely ignore this email.

– The Rental Command Team
""";

        // HTML body renders a single "Here" anchor instead of printing the full raw URL inline.
        var htmlBody = BuildAuthEmailHtml(
            greeting,
            introHtml: "Thanks for signing up for Rental Command! Please confirm your email — click",
            link: link,
            footerHtml: "This link will expire within 24 hours. If you didn't create an account, you can safely ignore this email.");

        await EnqueueAsync(user, expectedPortfolioId, subject, body, htmlBody, "email-confirmation", operationKey, ct);
        _logger.LogInformation("Enqueued email-confirmation email for {Email}.", user.Email);
    }

    public async Task SendPasswordResetAsync(
        ApplicationUser user,
        int? expectedPortfolioId,
        string token,
        string operationKey,
        CancellationToken ct = default)
    {
        var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
        var link = $"{webBase}/reset-password?userId={user.Id}&token={Uri.EscapeDataString(token)}";

        var greeting = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : user.Email ?? "there";
        var subject = "Reset your Rental Command password";

        // Plaintext fallback keeps the raw URL so a text-only client can still complete the flow.
        var body = $"""
Hi {greeting},

We received a request to reset the password for your Rental Command account. To set a new password, click the link below:

{link}

This link expires in 1 hour. If you didn't request a password reset, you can safely ignore this email — your password won't change.

– The Rental Command Team
""";

        // HTML body renders a single "Here" anchor instead of printing the full raw URL inline.
        var htmlBody = BuildAuthEmailHtml(
            greeting,
            introHtml: "We received a request to reset the password for your Rental Command account. To set a new password, click",
            link: link,
            footerHtml: "This link expires in 1 hour. If you didn't request a password reset, you can safely ignore this email — your password won't change.");

        await EnqueueAsync(user, expectedPortfolioId, subject, body, htmlBody, "password-reset", operationKey, ct);
        _logger.LogInformation("Enqueued password-reset email for {Email}.", user.Email);
    }

    private async Task EnqueueAsync(
        ApplicationUser user,
        int? expectedPortfolioId,
        string subject,
        string body,
        string htmlBody,
        string kind,
        string operationKey,
        CancellationToken ct)
    {
        // The outbox carries both a plaintext body (the source of truth / fallback) and an
        // optional htmlBody. The Engine's OutboxDispatchWorker passes both to the email transport;
        // SendGrid adds a text/html part and SMTP uses it as the HtmlBody. kind is retained for
        // logging/diagnostics only — the routing key stays "email".
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var payload = JsonSerializer.Serialize(new { to = user.Email!, subject, body, htmlBody });
        var operationDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)))
            .ToLowerInvariant();
        var intent = Encoding.UTF8.GetBytes($"{user.Id}\0{kind}\0{user.SecurityStamp}");
        var intentHash = Convert.ToHexString(SHA256.HashData(intent)).ToLowerInvariant();
        var command = new AuthEmailOutboxCommand(
            user.Id,
            expectedPortfolioId,
            user.SecurityStamp ?? string.Empty,
            kind,
            payload,
            intentHash,
            $"auth:{kind}:{user.Id}:{operationDigest}");
        _ = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity($"auth.email.{kind}", $"{user.Id}:{operationDigest}"),
            command,
            ResultCodec,
            ct);
    }

    /// <summary>
    /// Builds a minimal, safe HTML body for an auth email: a greeting, an intro sentence ending in a
    /// single <c>Here</c> hyperlink to <paramref name="link"/>, and a footer. The dynamic greeting is
    /// HTML-encoded; the intro/footer are fixed English copy supplied by this class (not user input).
    /// </summary>
    private static string BuildAuthEmailHtml(string greeting, string introHtml, string link, string footerHtml)
    {
        var safeGreeting = WebUtility.HtmlEncode(greeting);
        var safeLink = WebUtility.HtmlEncode(link);
        return $$"""
<!DOCTYPE html>
<html>
<body style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;line-height:1.6;color:#1a1a2e;">
  <p>Hi {{safeGreeting}},</p>
  <p>{{introHtml}} <a href="{{safeLink}}">Here</a>.</p>
  <p style="color:#6b7280;font-size:13px;">{{footerHtml}}</p>
  <p>– The Rental Command Team</p>
</body>
</html>
""";
    }
}
