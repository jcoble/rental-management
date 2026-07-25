using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Constants;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Periodically checks every worker's last heartbeat timestamp and logs a WARNING
/// when a worker has not checked in within its configured threshold. After
/// <see cref="RequiredConsecutiveDetections"/> consecutive stuck detections the watchdog
/// calls <see cref="IHostApplicationLifetime.StopApplication"/> so the process manager
/// (systemd / Docker) restarts a fresh instance.
///
/// Thresholds are defined in <see cref="WorkerHealthThresholds.WatchdogStuckSeconds"/>.
/// </summary>
public class WorkerWatchdogService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of consecutive stuck detections required before triggering a restart.
    /// Prevents false-positive restarts from transient slow DB queries or brief
    /// processing spikes. At 30 s check interval, 3 consecutive = ~90 s confirmed stuck.
    /// </summary>
    private const int RequiredConsecutiveDetections = 3;

    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WorkerWatchdogService> _logger;
    private readonly Dictionary<string, int> _consecutiveStuckCounts = new();

    public WorkerWatchdogService(
        IServiceProvider serviceProvider,
        IHostApplicationLifetime lifetime,
        ILogger<WorkerWatchdogService> logger)
    {
        _serviceProvider = serviceProvider;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WorkerWatchdog started — checking every {Interval}s, " +
            "requires {Consecutive} consecutive stuck detections before restart",
            CheckInterval.TotalSeconds, RequiredConsecutiveDetections);

        // Give workers time to start and record their first heartbeat.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckWorkersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WorkerWatchdog: error during heartbeat check");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckWorkersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

        var now = DateTime.UtcNow;
        var observations = await WorkerWatchdogEligibilityQuery
            .Create(context, now)
            .ToListAsync(ct);

        foreach (var observation in observations)
        {
            if (observation.IsUnhealthy)
            {
                _consecutiveStuckCounts.TryGetValue(observation.WorkerName, out var count);
                count++;
                _consecutiveStuckCounts[observation.WorkerName] = count;

                var age = (now - observation.LastHeartbeatUtc).TotalSeconds;
                var stuckThreshold = WorkerHealthThresholds.WatchdogStuckSeconds[observation.WorkerName];

                // Describe WHY the worker is considered unhealthy for clearer logs.
                var reason = observation.IsErrored
                    ? (observation.IsStalled
                        ? $"reported Error status and last heartbeat {age:F0}s ago (threshold: {stuckThreshold}s)"
                        : "reported Error status")
                    : $"last heartbeat {age:F0}s ago (threshold: {stuckThreshold}s)";

                if (count < RequiredConsecutiveDetections)
                {
                    _logger.LogWarning(
                        "WorkerWatchdog: {WorkerName} appears unhealthy — {Reason}. " +
                        "Detection {Count}/{Required} before restart.",
                        observation.WorkerName, reason, count, RequiredConsecutiveDetections);
                    continue;
                }

                _logger.LogCritical(
                    "WorkerWatchdog: {WorkerName} confirmed unhealthy after {Count} consecutive detections — " +
                    "{Reason}. Triggering engine restart.",
                    observation.WorkerName, count, reason);

                _lifetime.StopApplication();
                return;
            }
            else
            {
                // Worker is healthy — reset its consecutive stuck count.
                _consecutiveStuckCounts.Remove(observation.WorkerName);
            }
        }
    }
}
