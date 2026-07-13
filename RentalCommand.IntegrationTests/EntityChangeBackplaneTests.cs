using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RentalCommand.Api.Services;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Engine.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Proves the TSK-624 realtime backplane end to end across two independent PostgreSQL connections
/// with a real database in the middle — the exact cross-process shape of API + Engine in production.
/// <para>
/// The Engine's <see cref="NotifyDataUpdateService"/> (publisher) and the API's
/// <see cref="EntityChangeListener"/> (LISTEN broadcaster) run against the same containerised
/// Postgres. Publishing a change on the Engine side must travel Engine → <c>pg_notify</c> → the API
/// listener's dedicated <c>LISTEN</c> connection → the hub-backed <see cref="IDataUpdateService"/>.
/// A capturing fake stands in for the SignalR hub so the assertion is on what the hub would emit.
/// This is the guarantee the previous no-op silently broke.
/// </para>
/// </summary>
public sealed class EntityChangeBackplaneTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _connString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
            _connString = _pg.GetConnectionString();
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        // The production listener and publisher deliberately SET ROLE before LISTEN/NOTIFY.
        // Apply the canonical foundation so this isolated database has those runtime roles and
        // grants instead of silently exercising a pre-role-boundary environment.
        await using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(_connString)
                .Options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task EngineNotify_ReachesApiListener_AndReBroadcasts_AcrossProcesses()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; SignalR backplane cross-process verification skipped.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // --- API side: the real listener, with a capturing fake in place of the SignalR hub ---
        var capture = new CapturingDataUpdateService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataUpdateService>(capture);
        await using var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connString,
            })
            .Build();

        var listener = new EntityChangeListener(
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<EntityChangeListener>.Instance);
        await listener.StartAsync(cts.Token);

        // --- Engine side: the real publisher on its own data source (a separate connection pool) ---
        await using var dataSource = new NpgsqlDataSourceBuilder(_connString).Build();
        var publisher = new NotifyDataUpdateService(dataSource, NullLogger<NotifyDataUpdateService>.Instance);

        try
        {
            // 1) UPDATE with data. Retry-publish until captured: LISTEN/NOTIFY drops anything sent before
            //    the listener's LISTEN is established, and StartAsync does not await that. A Unit update is
            //    the one case the client reads `data` for (the propertyId derived key), so it also proves
            //    the data payload survives the Engine → NOTIFY → LISTEN → hub round-trip.
            var update = await PublishUntilCapturedAsync(
                capture,
                () => publisher.BroadcastEntityUpdateAsync(7, "Unit", 42, new { propertyId = 99 }, cts.Token),
                cts.Token);

            update.Op.Should().Be("u");
            update.PortfolioId.Should().Be(7);
            update.EntityType.Should().Be("Unit");
            update.EntityId.Should().Be(42);
            var data = update.Data.Should().BeOfType<JsonElement>().Subject;
            data.GetProperty("propertyId").GetInt32().Should().Be(99);

            // 2) DELETE. The connection is warm now, so a single publish is observed promptly.
            await publisher.BroadcastEntityDeleteAsync(7, "WorkOrder", 5, cts.Token);
            var delete = await capture.Broadcasts.Reader.ReadAsync(cts.Token);

            delete.Op.Should().Be("d");
            delete.PortfolioId.Should().Be(7);
            delete.EntityType.Should().Be("WorkOrder");
            delete.EntityId.Should().Be(5);
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Publishes repeatedly until the listener captures a broadcast, defeating the startup race where
    /// a NOTIFY sent before the LISTEN is established is silently lost. Duplicates from earlier attempts
    /// are harmless — the first captured broadcast is returned.
    /// </summary>
    private static async Task<Broadcast> PublishUntilCapturedAsync(
        CapturingDataUpdateService capture, Func<Task> publish, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await publish();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
                return await capture.Broadcasts.Reader.ReadAsync(attempt.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Listener not LISTENing yet — publish again.
            }
        }
    }

    private sealed record Broadcast(string Op, int PortfolioId, string EntityType, int EntityId, object? Data);

    private sealed class CapturingDataUpdateService : IDataUpdateService
    {
        public readonly System.Threading.Channels.Channel<Broadcast> Broadcasts =
            System.Threading.Channels.Channel.CreateUnbounded<Broadcast>();

        public Task BroadcastEntityUpdateAsync(
            int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
        {
            Broadcasts.Writer.TryWrite(new Broadcast("u", portfolioId, entityType, entityId, data));
            return Task.CompletedTask;
        }

        public Task BroadcastEntityDeleteAsync(
            int portfolioId, string entityType, int entityId, CancellationToken ct = default)
        {
            Broadcasts.Writer.TryWrite(new Broadcast("d", portfolioId, entityType, entityId, null));
            return Task.CompletedTask;
        }
    }
}
