namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Stages messages in the current unit of work's reliable DB outbox. The caller owns the commit so
/// business changes and every staged message are persisted by one atomic save/transaction. The
/// Engine later dispatches committed rows via an <see cref="INotificationChannel"/>.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Stage a message of the given type with a JSON-serializable payload for the given portfolio.
    /// This method never calls SaveChanges; the caller must commit the shared DbContext.
    /// </summary>
    Task PublishAsync<TPayload>(
        int portfolioId,
        string messageType,
        string idempotencyKey,
        TPayload payload,
        CancellationToken ct = default);
}
