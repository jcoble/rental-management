namespace RentalCommand.Core.Interfaces;

public sealed record EntityUpdateBroadcast(int PortfolioId, string EntityType, int EntityId, object Data);

public sealed record SavedContextNotificationRealtimeHint();

public interface IRealtimeInvalidationQueue
{
    void EnqueueEntityUpdates(IReadOnlyList<EntityUpdateBroadcast> updates);
}

/// <summary>
/// Broadcasts realtime entity changes to connected clients (backed by SignalR in the Api).
/// Phase 0 defines the contract only; the SignalR-backed implementation lands in the hubs wave.
/// </summary>
public interface IDataUpdateService
{
    /// <summary>
    /// Notify currently authorized sessions that an entity was created or updated. Implementations
    /// must derive the audience from durable resource scope; <paramref name="data"/> is an internal
    /// publisher hint and must not be treated as an authorization source or forwarded as a full DTO.
    /// </summary>
    Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default);

    /// <summary>
    /// Notify currently authorized sessions about a post-commit batch of entity updates. The default
    /// preserves existing implementations; the SignalR-backed implementation can bound hub delivery
    /// for the whole batch instead of serializing one wait per update.
    /// </summary>
    async Task BroadcastEntityUpdatesAsync(
        IReadOnlyList<EntityUpdateBroadcast> updates,
        CancellationToken ct = default)
    {
        foreach (var update in updates)
        {
            await BroadcastEntityUpdateAsync(
                update.PortfolioId,
                update.EntityType,
                update.EntityId,
                update.Data,
                ct);
        }
    }

    /// <summary>Notify currently authorized sessions that an entity was deleted.</summary>
    Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default);
}
