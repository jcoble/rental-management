using Microsoft.AspNetCore.SignalR;
using RentalCommand.Api.Hubs;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services;

/// <summary>
/// SignalR-backed implementation of <see cref="IDataUpdateService"/>. Broadcasts entity create/update
/// and delete events to the <c>portfolio-{portfolioId}</c> group on <see cref="DataUpdateHub"/> so all
/// connected members of a portfolio can refresh affected data. Failures are logged and swallowed so a
/// realtime hiccup never breaks the originating write operation.
/// </summary>
public class DataUpdateService : IDataUpdateService
{
    private readonly IHubContext<DataUpdateHub> _hubContext;
    private readonly ILogger<DataUpdateService> _logger;

    public DataUpdateService(IHubContext<DataUpdateHub> hubContext, ILogger<DataUpdateService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task BroadcastEntityUpdateAsync(
        int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
    {
        try
        {
            var payload = new EntityUpdatePayload
            {
                EntityType = entityType,
                EntityId = entityId,
                Data = data,
                Timestamp = DateTime.UtcNow,
            };

            await _hubContext.Clients
                .Group($"portfolio-{portfolioId}")
                .SendAsync("EntityUpdated", payload, ct);

            _logger.LogDebug(
                "Broadcast EntityUpdated {EntityType} {EntityId} to portfolio {PortfolioId}",
                entityType, entityId, portfolioId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to broadcast EntityUpdated {EntityType} {EntityId} to portfolio {PortfolioId}",
                entityType, entityId, portfolioId);
        }
    }

    public async Task BroadcastEntityDeleteAsync(
        int portfolioId, string entityType, int entityId, CancellationToken ct = default)
    {
        try
        {
            var payload = new EntityDeletePayload
            {
                EntityType = entityType,
                EntityId = entityId,
                Timestamp = DateTime.UtcNow,
            };

            await _hubContext.Clients
                .Group($"portfolio-{portfolioId}")
                .SendAsync("EntityDeleted", payload, ct);

            _logger.LogDebug(
                "Broadcast EntityDeleted {EntityType} {EntityId} to portfolio {PortfolioId}",
                entityType, entityId, portfolioId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to broadcast EntityDeleted {EntityType} {EntityId} to portfolio {PortfolioId}",
                entityType, entityId, portfolioId);
        }
    }
}
