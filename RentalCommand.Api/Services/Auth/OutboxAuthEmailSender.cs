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
            var body = $"""
Hi {greeting},

Thanks for signing up for Rental Command! Please confirm your email address by clicking (or copying) the link below:

{link}

This link will expire within 24 hours. If you didn't create an account, you can safely ignore this email.

– The Rental Command Team
""";

            await EnqueueAsync(user.Email!, subject, body, "email-confirmation", ct);
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
            var body = $"""
Hi {greeting},

We received a request to reset the password for your Rental Command account. Use the link below to set a new password:

{link}

This link expires in 1 hour. If you didn't request a password reset, you can safely ignore this email — your password won't change.

– The Rental Command Team
""";

            await EnqueueAsync(user.Email!, subject, body, "password-reset", ct);
            _logger.LogInformation("Enqueued password-reset email for {Email}.", user.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue password-reset email for user {UserId}.", user.Id);
        }
    }

    private async Task EnqueueAsync(string to, string subject, string body, string kind, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { to, subject, body });

        _db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = null,
            MessageType = "email",
            Payload = payload,
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}
