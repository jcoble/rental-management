using System.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Monitors the dedicated advisory lock connection held by this Engine instance.
/// If another Engine calls <c>pg_terminate_backend()</c> on our lock connection
/// (the takeover flow), this watcher detects the dead connection and triggers
/// graceful shutdown via <see cref="IHostApplicationLifetime.StopApplication"/>.
///
/// Without this, the old Engine process would keep running as a zombie indefinitely
/// after a newer instance takes over.
/// </summary>
public class AdvisoryLockWatcherService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AdvisoryLockWatcherService> _logger;

    public AdvisoryLockWatcherService(
        IHostApplicationLifetime lifetime,
        ILogger<AdvisoryLockWatcherService> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The lock connection is set in Program.cs after host.Build() returns.
        // Wait up to 60 s for it to appear before giving up.
        var waitCount = 0;
        while (Program.AdvisoryLockConnection == null && !stoppingToken.IsCancellationRequested)
        {
            if (++waitCount > 60)
            {
                _logger.LogError(
                    "AdvisoryLockWatcher: lock connection was not established within 60 seconds — shutting down");
                _lifetime.StopApplication();
                return;
            }

            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        _logger.LogInformation(
            "AdvisoryLockWatcher started — monitoring lock connection (PID {Pid})",
            Environment.ProcessId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, stoppingToken);

                var conn = Program.AdvisoryLockConnection;
                if (conn == null || conn.State != ConnectionState.Open)
                {
                    // If the stopping token is already cancelled the ApplicationStopping
                    // handler closed the connection — that's a normal shutdown, not a takeover.
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    _logger.LogCritical(
                        "AdvisoryLockWatcher: lock connection lost (State={State}) — " +
                        "another Engine instance has taken over. Shutting down (PID {Pid}).",
                        conn?.State, Environment.ProcessId);
                    _lifetime.StopApplication();
                    return;
                }

                // Ping the connection to verify it is truly alive (not just cached state).
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                _logger.LogCritical(ex,
                    "AdvisoryLockWatcher: lock connection check failed — " +
                    "another Engine instance has taken over. Shutting down (PID {Pid}).",
                    Environment.ProcessId);
                _lifetime.StopApplication();
                return;
            }
        }
    }
}
