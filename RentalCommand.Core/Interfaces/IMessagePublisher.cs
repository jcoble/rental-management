namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Publishes messages onto the reliable DB-outbox. The Engine later dispatches them via an
/// <see cref="INotificationChannel"/>. Phase 0 defines the contract only; the DB-backed
/// implementation lands in the Engine wave.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Enqueue a message of the given type with a JSON-serializable payload for the given portfolio.
    /// </summary>
    Task PublishAsync<TPayload>(
        int portfolioId,
        string messageType,
        TPayload payload,
        CancellationToken ct = default);
}
