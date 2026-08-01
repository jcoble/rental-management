using Microsoft.Extensions.Logging;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Local/test notification sink. It accepts outbox deliveries without calling paid external
/// providers, so simulation and ordinary development runs can drain the queue safely.
/// </summary>
public sealed class CapturedNotificationChannel : INotificationChannel
{
    private readonly ILogger<CapturedNotificationChannel> _logger;

    public CapturedNotificationChannel(ILogger<CapturedNotificationChannel> logger) => _logger = logger;

    public Task<NotificationDeliveryReceipt> SendSmsAsync(
        string toPhoneNumber,
        string message,
        NotificationDeliveryContext delivery,
        int? portfolioId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[SMS captured locally] OutboxMessageId={OutboxMessageId} PortfolioId={PortfolioId} To={To}",
            delivery.OutboxMessageId, portfolioId, toPhoneNumber);
        return Task.FromResult(Receipt("sms", delivery));
    }

    public Task<NotificationDeliveryReceipt> SendEmailAsync(
        string toEmail,
        string subject,
        string body,
        NotificationDeliveryContext delivery,
        string? htmlBody = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Email captured locally] OutboxMessageId={OutboxMessageId} To={To} Subject={Subject}",
            delivery.OutboxMessageId, toEmail, subject);
        return Task.FromResult(Receipt("email", delivery));
    }

    private static NotificationDeliveryReceipt Receipt(string channel, NotificationDeliveryContext delivery) =>
        new("local-capture", $"captured-{channel}-{delivery.OutboxMessageId}");
}
