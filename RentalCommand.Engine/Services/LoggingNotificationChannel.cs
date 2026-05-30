using Microsoft.Extensions.Logging;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Phase 0 stub <see cref="INotificationChannel"/>: logs the outbound SMS/email instead of
/// hitting a real transport. Lets the outbox dispatch loop run end-to-end (rows get marked
/// <c>SentAt</c>) without any external dependency. Concrete Twilio/SendGrid-style providers
/// replace this in Phase 4.
/// </summary>
public sealed class LoggingNotificationChannel : INotificationChannel
{
    private readonly ILogger<LoggingNotificationChannel> _logger;

    public LoggingNotificationChannel(ILogger<LoggingNotificationChannel> logger)
    {
        _logger = logger;
    }

    public Task SendSmsAsync(string toPhoneNumber, string message, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[stub-sms] To={To} Message={Message}", toPhoneNumber, message);
        return Task.CompletedTask;
    }

    public Task SendEmailAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[stub-email] To={To} Subject={Subject} Body={Body}", toEmail, subject, body);
        return Task.CompletedTask;
    }
}
