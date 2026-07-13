using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Realtime;

namespace RentalCommand.Engine.Services;

/// <summary>
/// <see cref="IDataUpdateService"/> for the Engine host. The Engine is out-of-process and cannot
/// reach the API's in-memory SignalR hub directly, so it publishes each entity change as a
/// PostgreSQL <c>NOTIFY</c> on <see cref="DataUpdateNotification.ChannelName"/>. The API-hosted
/// <c>EntityChangeListener</c> is <c>LISTEN</c>ing on that channel and re-broadcasts to the hub, so
/// automation-driven changes (rent charges, late fees, recurring items, notices, scan completion,
/// …) now reach connected browsers live — this replaces the previous no-op that dropped them.
/// <para>
/// Publishing runs on its own pooled connection (via <see cref="NpgsqlDataSource"/>), independent of
/// the caller's DbContext transaction, and every failure is caught and logged — a realtime hiccup
/// must never break the originating write, exactly as the API-side broadcaster behaves.
/// </para>
/// </summary>
public sealed class NotifyDataUpdateService : IDataUpdateService
{
    // PostgreSQL rejects a NOTIFY payload of 8000 bytes or more. Stay comfortably under that; if the
    // serialized event would exceed this, we re-send without Data. Data is only an internal publisher
    // hint; the API resolves recipients from the database and emits a minimal invalidation payload.
    private const int MaxPayloadBytes = 7000;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<NotifyDataUpdateService> _logger;

    public NotifyDataUpdateService(NpgsqlDataSource dataSource, ILogger<NotifyDataUpdateService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task BroadcastEntityUpdateAsync(
        int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default) =>
        PublishAsync(
            new DataUpdateNotification
            {
                Op = DataUpdateNotification.OpUpdate,
                PortfolioId = portfolioId,
                EntityType = entityType,
                EntityId = entityId,
                Data = data,
            },
            ct);

    /// <inheritdoc />
    public Task BroadcastEntityDeleteAsync(
        int portfolioId, string entityType, int entityId, CancellationToken ct = default) =>
        PublishAsync(
            new DataUpdateNotification
            {
                Op = DataUpdateNotification.OpDelete,
                PortfolioId = portfolioId,
                EntityType = entityType,
                EntityId = entityId,
                Data = null,
            },
            ct);

    private async Task PublishAsync(DataUpdateNotification notification, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(notification, DataUpdateNotification.SerializerOptions);

            if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes && notification.Data is not null)
            {
                _logger.LogWarning(
                    "Data-update payload for {EntityType} {EntityId} exceeded {Limit} bytes; " +
                    "publishing without Data (client widens invalidation).",
                    notification.EntityType, notification.EntityId, MaxPayloadBytes);
                notification.Data = null;
                payload = JsonSerializer.Serialize(notification, DataUpdateNotification.SerializerOptions);
            }

            // pg_notify() (the function form) takes the channel + payload as bind parameters, avoiding
            // the identifier/string-literal escaping that the bare NOTIFY statement would require.
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using (var roleCommand = new NpgsqlCommand("SET ROLE rentalcommand_engine", conn))
            {
                await roleCommand.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
            cmd.Parameters.AddWithValue("channel", DataUpdateNotification.ChannelName);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);

            _logger.LogDebug(
                "Published data-update NOTIFY {Op} {EntityType} {EntityId} for portfolio {PortfolioId}",
                notification.Op, notification.EntityType, notification.EntityId, notification.PortfolioId);
        }
        catch (Exception ex)
        {
            // Never let a realtime publish failure break the worker's write path.
            _logger.LogError(ex,
                "Failed to publish data-update NOTIFY {Op} {EntityType} {EntityId} for portfolio {PortfolioId}",
                notification.Op, notification.EntityType, notification.EntityId, notification.PortfolioId);
        }
    }
}
