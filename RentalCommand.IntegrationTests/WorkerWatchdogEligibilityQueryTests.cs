using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Engine.Workers;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for the watchdog's bounded, database-side eligibility query.</summary>
public sealed class WorkerWatchdogEligibilityQueryTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _dockerAvailable = true;
        _connectionString = _postgres.GetConnectionString();

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Eligibility_is_computed_ordered_and_bounded_by_postgres()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;

        await using (var seed = NewContext())
        {
            seed.EngineWorkerHeartbeats.AddRange(
                Heartbeat("OutboxDispatchWorker", now.AddSeconds(-121), EngineWorkerStatus.Running),
                Heartbeat("ScanProcessingWorker", now.AddSeconds(-121), EngineWorkerStatus.Running),
                Heartbeat("RentChargeWorker", now.AddSeconds(-1), EngineWorkerStatus.Error),
                Heartbeat("UnknownWorker", now.AddDays(-1), EngineWorkerStatus.Error));
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var query = WorkerWatchdogEligibilityQuery.Create(db, now);
        var sql = query.ToQueryString();
        var observations = await query.ToListAsync();

        sql.Should().Contain("WHERE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("LastHeartbeatUtc");

        observations.Select(row => row.WorkerName).Should().Equal(
            "OutboxDispatchWorker",
            "RentChargeWorker",
            "ScanProcessingWorker");

        var staleOutbox = observations.Single(row => row.WorkerName == "OutboxDispatchWorker");
        staleOutbox.IsStalled.Should().BeTrue();
        staleOutbox.IsErrored.Should().BeFalse();
        staleOutbox.IsUnhealthy.Should().BeTrue();

        var healthyScan = observations.Single(row => row.WorkerName == "ScanProcessingWorker");
        healthyScan.IsStalled.Should().BeFalse();
        healthyScan.IsErrored.Should().BeFalse();
        healthyScan.IsUnhealthy.Should().BeFalse();

        var erroredRent = observations.Single(row => row.WorkerName == "RentChargeWorker");
        erroredRent.IsStalled.Should().BeFalse();
        erroredRent.IsErrored.Should().BeTrue();
        erroredRent.IsUnhealthy.Should().BeTrue();
    }

    private static EngineWorkerHeartbeat Heartbeat(
        string workerName,
        DateTime lastHeartbeatUtc,
        EngineWorkerStatus status) =>
        new()
        {
            WorkerName = workerName,
            LastHeartbeatUtc = lastHeartbeatUtc,
            Status = status,
            StartedAtUtc = lastHeartbeatUtc,
        };

    private RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);
}
