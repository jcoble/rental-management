using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Persists worker health records (start, heartbeat, error) to the database.
/// Called from <see cref="Workers.EngineWorkerBase"/> on each poll cycle and on errors.
/// Registered as <b>Scoped</b>; its kernel-owned infrastructure transaction uses that scope's
/// DbContext and performs each concurrent-safe upsert as one PostgreSQL statement.
/// </summary>
public class EngineStatusReporter
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicInfrastructureUnitOfWork _infrastructure;
    private readonly ILogger<EngineStatusReporter> _logger;

    public EngineStatusReporter(
        RentalCommandDbContext db,
        IAtomicInfrastructureUnitOfWork infrastructure,
        ILogger<EngineStatusReporter> logger)
    {
        _db = db;
        _infrastructure = infrastructure;
        _logger = logger;
    }

    /// <summary>
    /// Called when a worker first starts. Upserts the heartbeat row with
    /// <c>Status = Running</c>, resets cycle/processed counts, and stamps
    /// <c>StartedAtUtc</c>.
    /// </summary>
    public async Task ReportStartAsync(string workerName, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _infrastructure.ExecuteAsync(
                AtomicInfrastructureOperation.EngineHeartbeat,
                async ct =>
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                        INSERT INTO "EngineWorkerHeartbeats"
                            ("WorkerName", "LastHeartbeatUtc", "Status", "StartedAtUtc",
                             "ProcessedCount", "CycleCount")
                        VALUES
                            ({{workerName}}, {{now}}, {{(int)EngineWorkerStatus.Running}}, {{now}}, 0, 0)
                        ON CONFLICT ("WorkerName") DO UPDATE SET
                            "StartedAtUtc" = EXCLUDED."StartedAtUtc",
                            "LastHeartbeatUtc" = EXCLUDED."LastHeartbeatUtc",
                            "Status" = EXCLUDED."Status",
                            "ProcessedCount" = 0,
                            "CycleCount" = 0
                        """, ct);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EngineStatusReporter: failed to record start for {WorkerName}", workerName);
        }
    }

    /// <summary>
    /// Called after a successful poll cycle. Upserts the heartbeat row with the current
    /// timestamp, increments cycle/processed counts, and embeds PID + lock metadata.
    /// Clears any stale error fields so the admin UI reflects recovery.
    /// </summary>
    public async Task ReportHeartbeatAsync(
        string workerName,
        CancellationToken cancellationToken,
        long processedDelta = 0)
    {
        try
        {
            var now = DateTime.UtcNow;
            var metadata = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                hostname = Environment.MachineName,
                version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                advisoryLockHeld = Program.AdvisoryLockHeld,
                lockContested = Program.LockContested
            });

            await _infrastructure.ExecuteAsync(
                AtomicInfrastructureOperation.EngineHeartbeat,
                async ct =>
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                        INSERT INTO "EngineWorkerHeartbeats"
                            ("WorkerName", "LastHeartbeatUtc", "Status", "StartedAtUtc",
                             "ProcessedCount", "CycleCount", "Metadata")
                        VALUES
                            ({{workerName}}, {{now}}, {{(int)EngineWorkerStatus.Running}}, {{now}},
                             {{processedDelta}}, 1, CAST({{metadata}} AS jsonb))
                        ON CONFLICT ("WorkerName") DO UPDATE SET
                            "LastHeartbeatUtc" = EXCLUDED."LastHeartbeatUtc",
                            "Status" = EXCLUDED."Status",
                            "ProcessedCount" = "EngineWorkerHeartbeats"."ProcessedCount" + EXCLUDED."ProcessedCount",
                            "CycleCount" = "EngineWorkerHeartbeats"."CycleCount" + 1,
                            "Metadata" = EXCLUDED."Metadata",
                            "LastErrorMessage" = NULL,
                            "LastErrorUtc" = NULL
                        """, ct);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EngineStatusReporter: failed to record heartbeat for {WorkerName}", workerName);
        }
    }

    /// <summary>
    /// Called when a worker cycle throws. Sets <c>Status = Error</c> and records the
    /// exception message and timestamp.
    /// </summary>
    public async Task ReportErrorAsync(Exception ex, string workerName, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _infrastructure.ExecuteAsync(
                AtomicInfrastructureOperation.EngineHeartbeat,
                async ct =>
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                        INSERT INTO "EngineWorkerHeartbeats"
                            ("WorkerName", "LastHeartbeatUtc", "LastErrorMessage", "LastErrorUtc",
                             "Status", "ProcessedCount", "StartedAtUtc", "CycleCount")
                        VALUES
                            ({{workerName}}, {{now}}, {{ex.Message}}, {{now}},
                             {{(int)EngineWorkerStatus.Error}}, 0, {{now}}, 0)
                        ON CONFLICT ("WorkerName") DO UPDATE SET
                            "Status" = EXCLUDED."Status",
                            "LastErrorMessage" = EXCLUDED."LastErrorMessage",
                            "LastErrorUtc" = EXCLUDED."LastErrorUtc"
                        """, ct);
                },
                cancellationToken);
        }
        catch (Exception loggingEx)
        {
            _logger.LogError(loggingEx, "EngineStatusReporter: failed to record error for {WorkerName}", workerName);
        }
    }
}
