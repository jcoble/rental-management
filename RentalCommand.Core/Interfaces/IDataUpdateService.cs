namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Broadcasts realtime entity changes to connected clients (backed by SignalR in the Api).
/// Phase 0 defines the contract only; the SignalR-backed implementation lands in the hubs wave.
/// </summary>
public interface IDataUpdateService
{
    /// <summary>Notify the portfolio-{portfolioId} group that an entity was created or updated.</summary>
    Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default);

    /// <summary>Notify the portfolio-{portfolioId} group that an entity was deleted.</summary>
    Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default);
}
