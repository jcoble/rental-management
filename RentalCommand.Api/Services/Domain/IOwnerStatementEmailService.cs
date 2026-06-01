namespace RentalCommand.Api.Services.Domain;

/// <summary>Result of an attempt to email an owner statement.</summary>
public record StatementEmailResult(bool Sent, string? Reason = null);

/// <summary>
/// Renders an owner's annual statement as plain text and enqueues it for delivery
/// via the DB outbox. The actual send is handled by the Engine's OutboxDispatchWorker.
/// </summary>
public interface IOwnerStatementEmailService
{
    /// <summary>
    /// Loads the owner statement for <paramref name="ownerId"/> / <paramref name="year"/>,
    /// renders a plain-text email body, and enqueues an outbox message for delivery.
    /// Returns {Sent:false} if the owner has no email address or no statement data exists.
    /// </summary>
    Task<StatementEmailResult> SendOwnerStatementAsync(
        int portfolioId,
        int ownerId,
        int year,
        CancellationToken ct = default);
}
