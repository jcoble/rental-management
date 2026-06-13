namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over an outbound notification transport (SMS/email). The Engine routes
/// <see cref="Entities.OutboxMessage"/> entries to a channel. Phase 0 defines the contract
/// only; concrete SMS/email providers land in Phase 4.
/// </summary>
public interface INotificationChannel
{
    /// <summary>
    /// Send an SMS message to a phone number on behalf of <paramref name="portfolioId"/>. The
    /// portfolio's own configured SMS provider (BYO creds) is used when present, otherwise the
    /// platform-level fallback. Null portfolio = platform-level send.
    /// </summary>
    Task SendSmsAsync(string toPhoneNumber, string message, int? portfolioId = null, CancellationToken ct = default);

    /// <summary>Send an email message.</summary>
    Task SendEmailAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}
