using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Persists worker health records (start, heartbeat, error) to the database.
/// Called from <see cref="Workers.EngineWorkerBase"/> on each poll cycle and on errors.
/// Registered as <b>Scoped</b> — each call site creates a fresh scope to isolate DB access.
/// </summary>
public class EngineStatusReporter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EngineStatusReporter> _logger;

    public EngineStatusReporter(IServiceProvider serviceProvider, ILogger<EngineStatusReporter> logger)
    {
        _serviceProvider = serviceProvider;
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
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

            var heartbeat = await context.EngineWorkerHeartbeats
                .FirstOrDefaultAsync(h => h.WorkerName == workerName, cancellationToken);

            var now = DateTime.UtcNow;
            if (heartbeat == null)
            {
                heartbeat = new EngineWorkerHeartbeat
                {
                    WorkerName = workerName,
                    LastHeartbeatUtc = now,
                    Status = EngineWorkerStatus.Running,
                    StartedAtUtc = now,
                    ProcessedCount = 0,
                    CycleCount = 0
                };
                context.EngineWorkerHeartbeats.Add(heartbeat);
            }
            else
            {
                heartbeat.StartedAtUtc = now;
                heartbeat.LastHeartbeatUtc = now;
                heartbeat.Status = EngineWorkerStatus.Running;
                heartbeat.ProcessedCount = 0;
                heartbeat.CycleCount = 0;
            }

            await context.SaveChangesAsync(cancellationToken);
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
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

            var heartbeat = await context.EngineWorkerHeartbeats
                .FirstOrDefaultAsync(h => h.WorkerName == workerName, cancellationToken);

            var now = DateTime.UtcNow;
            var metadata = JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                hostname = Environment.MachineName,
                version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                advisoryLockHeld = Program.AdvisoryLockHeld,
                lockContested = Program.LockContested
            });

            if (heartbeat == null)
            {
                heartbeat = new EngineWorkerHeartbeat
                {
                    WorkerName = workerName,
                    LastHeartbeatUtc = now,
                    Status = EngineWorkerStatus.Running,
                    StartedAtUtc = now,
                    ProcessedCount = processedDelta,
                    CycleCount = 1,
                    Metadata = metadata
                };
                context.EngineWorkerHeartbeats.Add(heartbeat);
            }
            else
            {
                heartbeat.LastHeartbeatUtc = now;
                heartbeat.Status = EngineWorkerStatus.Running;
                heartbeat.ProcessedCount += processedDelta;
                heartbeat.CycleCount++;
                heartbeat.Metadata = metadata;
                // Clear stale error fields on recovery
                heartbeat.LastErrorMessage = null;
                heartbeat.LastErrorUtc = null;
            }

            await context.SaveChangesAsync(cancellationToken);
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
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

            var heartbeat = await context.EngineWorkerHeartbeats
                .FirstOrDefaultAsync(h => h.WorkerName == workerName, cancellationToken);

            var now = DateTime.UtcNow;
            if (heartbeat == null)
            {
                heartbeat = new EngineWorkerHeartbeat
                {
                    WorkerName = workerName,
                    LastHeartbeatUtc = now,
                    Status = EngineWorkerStatus.Error,
                    LastErrorMessage = ex.Message,
                    LastErrorUtc = now,
                    StartedAtUtc = now
                };
                context.EngineWorkerHeartbeats.Add(heartbeat);
            }
            else
            {
                heartbeat.Status = EngineWorkerStatus.Error;
                heartbeat.LastErrorMessage = ex.Message;
                heartbeat.LastErrorUtc = now;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception loggingEx)
        {
            _logger.LogError(loggingEx, "EngineStatusReporter: failed to record error for {WorkerName}", workerName);
        }
    }
}
