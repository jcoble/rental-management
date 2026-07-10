using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Enqueues email-confirmation and password-reset emails via the DB outbox.
/// The Engine's <c>OutboxDispatchWorker</c> dispatches them through SendGrid (or logs
/// a suppressed send when SendGrid is not configured).
/// </summary>
public sealed class OutboxAuthEmailSender : IAuthEmailSender
{
    private readonly RentalCommandDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxAuthEmailSender> _logger;

    public OutboxAuthEmailSender(
        RentalCommandDbContext db,
        IConfiguration configuration,
        ILogger<OutboxAuthEmailSender> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailConfirmationAsync(ApplicationUser user, string token, CancellationToken ct = default)
    {
        try
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

            await EnqueueAsync(user.Email!, subject, body, htmlBody, "email-confirmation", ct);
            _logger.LogInformation("Enqueued email-confirmation email for {Email}.", user.Email);
        }
        catch (Exception ex)
        {
            // Email is best-effort — don't block registration on a transient DB hiccup.
            _logger.LogError(ex, "Failed to enqueue email-confirmation email for user {UserId}.", user.Id);
        }
    }

    public async Task SendPasswordResetAsync(ApplicationUser user, string token, CancellationToken ct = default)
    {
        try
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

            await EnqueueAsync(user.Email!, subject, body, htmlBody, "password-reset", ct);
            _logger.LogInformation("Enqueued password-reset email for {Email}.", user.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue password-reset email for user {UserId}.", user.Id);
        }
    }

    public async Task SendTeamInviteAsync(ApplicationUser user, string temporaryPassword, string roleName, CancellationToken ct = default)
    {
        try
        {
            var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
            var loginLink = $"{webBase}/login";

            var email = user.Email ?? string.Empty;
            var greeting = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : email;
            if (string.IsNullOrWhiteSpace(greeting)) greeting = "there";
            var subject = "You've been added to Rental Command";

            // Plaintext fallback carries the full credentials and the raw login URL so a
            // text-only client still has everything needed to sign in.
            var body = $"""
Hi {greeting},

You've been added to the team in Rental Command as {roleName}. Here's everything you need to sign in:

Email: {email}
Temporary password: {temporaryPassword}

Sign in here: {loginLink}

For your security, we recommend changing your password after you sign in for the first time.

– The Rental Command Team
""";

            // HTML body reuses the shared auth-email shell (greeting → intro + a single "Here" link →
            // footer). The credentials are interpolated into the intro, so HTML-encode them first —
            // BuildAuthEmailHtml inserts the intro raw, and an email/password is not trusted markup.
            var safeRole = WebUtility.HtmlEncode(roleName);
            var safeEmail = WebUtility.HtmlEncode(email);
            var safePassword = WebUtility.HtmlEncode(temporaryPassword);
            var introHtml =
                $"You've been added to the team in Rental Command as <strong>{safeRole}</strong>. " +
                $"Sign in with your email <strong>{safeEmail}</strong> and temporary password " +
                $"<strong>{safePassword}</strong>. To sign in, click";
            var htmlBody = BuildAuthEmailHtml(
                greeting,
                introHtml: introHtml,
                link: loginLink,
                footerHtml: "For your security, we recommend changing your password after you sign in for the first time.");

            await EnqueueAsync(email, subject, body, htmlBody, "team-invite", ct);
            _logger.LogInformation("Enqueued team-invite email for {Email}.", email);
        }
        catch (Exception ex)
        {
            // Email is best-effort — never block team-member creation on an email hiccup.
            _logger.LogError(ex, "Failed to enqueue team-invite email for user {UserId}.", user.Id);
        }
    }

    public async Task SendTenantPortalInviteAsync(ApplicationUser user, string temporaryPassword, CancellationToken ct = default)
    {
        try
        {
            var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
            var loginLink = $"{webBase}/login";

            var email = user.Email ?? string.Empty;
            var greeting = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : email;
            if (string.IsNullOrWhiteSpace(greeting)) greeting = "there";
            var subject = "Your resident portal access";

            // Plaintext fallback carries the full credentials and the raw login URL so a text-only
            // client still has everything needed to sign in.
            var body = $"""
Hi {greeting},

Your landlord has given you access to your resident portal, where you can view your lease, make payments, submit maintenance requests, and message your landlord.

Here's how to sign in:

Email: {email}
Temporary password: {temporaryPassword}

Sign in here: {loginLink}

For your security, we recommend changing your password after you sign in for the first time.

– The Rental Command Team
""";

            // HTML body reuses the shared auth-email shell. The credentials are interpolated into the
            // intro, so HTML-encode them first — BuildAuthEmailHtml inserts the intro raw.
            var safeEmail = WebUtility.HtmlEncode(email);
            var safePassword = WebUtility.HtmlEncode(temporaryPassword);
            var introHtml =
                "Your landlord has given you access to your <strong>resident portal</strong>, where you can " +
                "view your lease, make payments, submit maintenance requests, and message your landlord. " +
                $"Sign in with your email <strong>{safeEmail}</strong> and temporary password " +
                $"<strong>{safePassword}</strong>. To sign in, click";
            var htmlBody = BuildAuthEmailHtml(
                greeting,
                introHtml: introHtml,
                link: loginLink,
                footerHtml: "For your security, we recommend changing your password after you sign in for the first time.");

            await EnqueueAsync(email, subject, body, htmlBody, "tenant-portal-invite", ct);
            _logger.LogInformation("Enqueued tenant-portal-invite email for {Email}.", email);
        }
        catch (Exception ex)
        {
            // Email is best-effort — never block the staff invite action on an email hiccup.
            _logger.LogError(ex, "Failed to enqueue tenant-portal-invite email for user {UserId}.", user.Id);
        }
    }

    private async Task EnqueueAsync(string to, string subject, string body, string htmlBody, string kind, CancellationToken ct)
    {
        // The outbox carries both a plaintext body (the source of truth / fallback) and an
        // optional htmlBody. The Engine's OutboxDispatchWorker passes both to the email transport;
        // SendGrid adds a text/html part and SMTP uses it as the HtmlBody. kind is retained for
        // logging/diagnostics only — the routing key stays "email".
        var payload = JsonSerializer.Serialize(new { to, subject, body, htmlBody });
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var now = DateTime.UtcNow;

        _db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = null,
            MessageType = "email",
            Payload = payload,
            IdempotencyKey = $"auth:{kind}:{payloadHash}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

        await _db.SaveChangesAsync(ct);
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
