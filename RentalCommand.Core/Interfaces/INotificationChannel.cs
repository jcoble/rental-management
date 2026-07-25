namespace RentalCommand.Core.Interfaces;

using RentalCommand.Core.Outbox;

/// <summary>
/// Abstraction over an outbound notification transport (SMS/email). The Engine routes
/// <see cref="Entities.OutboxMessage"/> entries to a channel. Phase 0 defines the contract
/// only; concrete SMS/email providers land in Phase 4.
/// </summary>
public interface INotificationChannel : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>
    /// Send an SMS message to a phone number on behalf of <paramref name="portfolioId"/>. The
    /// portfolio's own configured SMS provider (BYO creds) is used when present, otherwise the
    /// platform-level fallback. Null portfolio = platform-level send.
    /// </summary>
    Task<NotificationDeliveryReceipt> SendSmsAsync(
        string toPhoneNumber,
        string message,
        NotificationDeliveryContext delivery,
        int? portfolioId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Send an email message. <paramref name="body"/> is the plaintext body (always present).
    /// <paramref name="htmlBody"/> is an optional pre-composed HTML alternative — when supplied it is
    /// used verbatim as the rich part (SendGrid text/html, SMTP HtmlBody); when null the transport
    /// falls back to its own minimal HTML wrapping of the plaintext.
    /// </summary>
    Task<NotificationDeliveryReceipt> SendEmailAsync(
        string toEmail,
        string subject,
        string body,
        NotificationDeliveryContext delivery,
        string? htmlBody = null,
        CancellationToken ct = default);
}
