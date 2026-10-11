using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Persists worker health records (start, heartbeat, error) to the database.
/// Called from <see cref="Workers.EngineWorkerBase"/> on each poll cycle and on errors.
/// Registered as <b>Scoped</b>; each concurrent-safe heartbeat upsert is performed by a Data-owned
/// store as one PostgreSQL statement.
/// </summary>
public class EngineStatusReporter
{
    private readonly IEngineWorkerHeartbeatStore _heartbeats;
    private readonly ILogger<EngineStatusReporter> _logger;

    public EngineStatusReporter(
        IEngineWorkerHeartbeatStore heartbeats,
        ILogger<EngineStatusReporter> logger)
    {
        _heartbeats = heartbeats;
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
            await _heartbeats.RecordStartAsync(workerName, now, cancellationToken);
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

            await _heartbeats.RecordHeartbeatAsync(workerName, now, processedDelta, metadata, cancellationToken);
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
            await _heartbeats.RecordErrorAsync(workerName, now, ex.Message, cancellationToken);
        }
        catch (Exception loggingEx)
        {
            _logger.LogError(loggingEx, "EngineStatusReporter: failed to record error for {WorkerName}", workerName);
        }
    }
}
