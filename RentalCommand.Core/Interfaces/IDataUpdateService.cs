namespace RentalCommand.Core.Interfaces;

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

    /// <summary>Notify currently authorized sessions that an entity was deleted.</summary>
    Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default);
}
