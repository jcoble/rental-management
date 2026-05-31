using Microsoft.Extensions.Logging;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Engine.Services;

/// <summary>
/// No-op <see cref="IDataUpdateService"/> for the Engine host.
/// <para>
/// The Engine is an out-of-process worker and cannot reach the Api's in-memory SignalR hub
/// directly. Cross-process SignalR broadcasting (e.g. via Redis backplane or an HTTP
/// notify-ready callback) is a deferred enhancement (Phase 5+). In the meantime the web
/// review page polls while a draft is <c>Pending</c>, so correctness is preserved without
/// any active push. This service satisfies the DI contract and logs at Debug so the worker
/// code path is unchanged.
/// </para>
/// </summary>
public sealed class EngineDataUpdateService : IDataUpdateService
{
    private readonly ILogger<EngineDataUpdateService> _logger;

    public EngineDataUpdateService(ILogger<EngineDataUpdateService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task BroadcastEntityUpdateAsync(
        int portfolioId,
        string entityType,
        int entityId,
        object data,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Engine data-update for {EntityType} {EntityId} (no-op; web uses bounded poll)",
            entityType, entityId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BroadcastEntityDeleteAsync(
        int portfolioId,
        string entityType,
        int entityId,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Engine data-delete for {EntityType} {EntityId} (no-op; web uses bounded poll)",
            entityType, entityId);
        return Task.CompletedTask;
    }
}
