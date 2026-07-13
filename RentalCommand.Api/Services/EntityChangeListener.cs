using System.Text.Json;
using System.Threading.Channels;
using Npgsql;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Realtime;

namespace RentalCommand.Api.Services;

/// <summary>
/// API-hosted realtime backplane bridge. Holds a dedicated PostgreSQL connection doing
/// <c>LISTEN <see cref="DataUpdateNotification.ChannelName"/></c> and, on each notification the Engine
/// publishes, re-broadcasts it on the SignalR hub via the hub-backed <see cref="IDataUpdateService"/>.
/// <para>
/// This is the cross-process bridge SignalR needs: the hub lives in this (API) process and is
/// in-memory per-process, so changes written by the out-of-process Engine cannot reach it directly.
/// The Engine <c>NOTIFY</c>s; this service <c>LISTEN</c>s and fans out. It mirrors EdiPlatform's
/// API-hosted broadcaster, using PostgreSQL LISTEN/NOTIFY in place of RabbitMQ (no new infra).
/// </para>
/// <para>
/// Resilience: the listen connection is dedicated and non-pooled (a LISTEN must own its backend) with
/// a keep-alive so idle drops are detected promptly; on any failure the service reconnects and
/// re-<c>LISTEN</c>s after a short delay. LISTEN/NOTIFY is not durable, so notifications emitted during
/// a reconnect window are lost — acceptable for a realtime refresh hint (the client also reconnects and
/// refetches on focus). Durable replay was the explicitly-deferred Option B.
/// </para>
/// </summary>
public sealed class EntityChangeListener : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EntityChangeListener> _logger;

    public EntityChangeListener(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<EntityChangeListener> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rawConnString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(rawConnString))
        {
            _logger.LogError(
                "EntityChangeListener disabled: no 'DefaultConnection' connection string. " +
                "Engine-originated realtime updates will not reach connected clients.");
            return;
        }

        // Dedicated, non-pooled backend for the LISTEN (a listen connection must own its session for the
        // host's lifetime). KeepAlive turns silent idle drops into a prompt failure we can reconnect on.
        var listenConnString = new NpgsqlConnectionStringBuilder(rawConnString)
        {
            Pooling = false,
            KeepAlive = 30,
        }.ToString();

        // Decouple the notification socket from the async SignalR fan-out: the LISTEN loop only enqueues
        // raw payloads (a fast, non-blocking write); a single reader drains and broadcasts them.
        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        var processor = ProcessQueueAsync(queue.Reader, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(listenConnString, queue.Writer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EntityChangeListener LISTEN connection failed; reconnecting in {Delay}s.",
                    ReconnectDelay.TotalSeconds);
                try
                {
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        queue.Writer.TryComplete();
        await processor;
    }

    private async Task ListenAsync(
        string connString, ChannelWriter<string> writer, CancellationToken stoppingToken)
    {
        await using var conn = new NpgsqlConnection(connString);

        void OnNotification(object? sender, NpgsqlNotificationEventArgs e) => writer.TryWrite(e.Payload);
        conn.Notification += OnNotification;

        try
        {
            await conn.OpenAsync(stoppingToken);

            await using (var cmd = new NpgsqlCommand(
                $"SET ROLE rentalcommand_api; LISTEN {DataUpdateNotification.ChannelName}", conn))
            {
                await cmd.ExecuteNonQueryAsync(stoppingToken);
            }

            _logger.LogInformation(
                "EntityChangeListener connected — LISTEN {Channel} (realtime backplane active).",
                DataUpdateNotification.ChannelName);

            // Blocks until a notification arrives (firing OnNotification) or the connection breaks
            // (throws — the caller reconnects). Cancellation on shutdown surfaces as OperationCanceled.
            while (!stoppingToken.IsCancellationRequested)
            {
                await conn.WaitAsync(stoppingToken);
            }
        }
        finally
        {
            conn.Notification -= OnNotification;
        }
    }

    private async Task ProcessQueueAsync(ChannelReader<string> reader, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var payload in reader.ReadAllAsync(stoppingToken))
            {
                await HandlePayloadAsync(payload, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task HandlePayloadAsync(string payload, CancellationToken ct)
    {
        DataUpdateNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<DataUpdateNotification>(
                payload, DataUpdateNotification.SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "EntityChangeListener: unparseable notification payload dropped: {Payload}", payload);
            return;
        }

        if (notification is null || string.IsNullOrEmpty(notification.EntityType))
        {
            _logger.LogWarning("EntityChangeListener: incomplete notification payload dropped: {Payload}", payload);
            return;
        }

        try
        {
            // Reuse the real hub-backed broadcaster so the wire payload/group naming is identical to
            // API-originated updates. It swallows its own hub errors, so this scope stays clean.
            using var scope = _scopeFactory.CreateScope();
            var dataUpdate = scope.ServiceProvider.GetRequiredService<IDataUpdateService>();

            if (notification.Op == DataUpdateNotification.OpDelete)
            {
                await dataUpdate.BroadcastEntityDeleteAsync(
                    notification.PortfolioId, notification.EntityType, notification.EntityId, ct);
            }
            else
            {
                await dataUpdate.BroadcastEntityUpdateAsync(
                    notification.PortfolioId, notification.EntityType, notification.EntityId,
                    notification.Data!, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EntityChangeListener: failed to re-broadcast {Op} {EntityType} {EntityId} for portfolio {PortfolioId}",
                notification.Op, notification.EntityType, notification.EntityId, notification.PortfolioId);
        }
    }
}
