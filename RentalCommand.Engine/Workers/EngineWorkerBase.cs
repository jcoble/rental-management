using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Base class for all engine workers.
/// Provides:
/// - A poll loop with a configurable interval.
/// - A fresh DI scope per cycle (so each cycle gets its own scoped DbContext + services).
/// - Per-cycle timeout via a linked <see cref="CancellationTokenSource"/>.
/// - Graceful shutdown on the host's stopping token.
/// - Heartbeat reporting to <see cref="EngineStatusReporter"/> on every successful cycle.
/// - Error reporting to <see cref="EngineStatusReporter"/> on cycle failure.
/// - Structured error logging that never lets one bad cycle kill the worker.
///
/// Single-instance safety (the PostgreSQL advisory lock) is enforced once at host
/// startup in Program.cs; the lock is released on graceful shutdown there. Workers
/// therefore assume they are the only Engine running against the database.
/// </summary>
public abstract class EngineWorkerBase : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    /// <summary>Human-readable worker name used in structured logs and heartbeat records.</summary>
    protected abstract string WorkerName { get; }

    /// <summary>Delay between poll cycles.</summary>
    protected abstract TimeSpan PollInterval { get; }

    /// <summary>Maximum time a single cycle may run before it is cancelled.</summary>
    protected abstract TimeSpan StepTimeout { get; }

    protected EngineWorkerBase(IServiceProvider serviceProvider, ILogger logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{WorkerName} starting (poll interval {Interval})", WorkerName, PollInterval);

        // Record start in the heartbeat table so the health check and watchdog
        // see this worker immediately rather than waiting for the first cycle.
        try
        {
            using var startScope = _serviceProvider.CreateScope();
            var reporter = startScope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
            await reporter.ReportStartAsync(WorkerName, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{WorkerName} failed to report start", WorkerName);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int itemsProcessed = 0;

            try
            {
                using var scope = _serviceProvider.CreateScope();

                // Per-cycle timeout linked to the host's stopping token.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(StepTimeout);

                itemsProcessed = await ExecuteCycleAsync(scope.ServiceProvider, timeoutCts.Token);

                if (itemsProcessed > 0)
                {
                    _logger.LogInformation("{WorkerName} processed {Count} item(s) this cycle", WorkerName, itemsProcessed);
                }
                else
                {
                    _logger.LogDebug("{WorkerName} cycle completed (no items)", WorkerName);
                }

                // Report successful heartbeat after every cycle (healthy or idle).
                var reporter = scope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
                await reporter.ReportHeartbeatAsync(WorkerName, stoppingToken, processedDelta: itemsProcessed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-cycle timeout (not a shutdown). Log and report.
                _logger.LogWarning("{WorkerName} cycle timed out after {Timeout}", WorkerName, StepTimeout);

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reporter = scope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
                    await reporter.ReportErrorAsync(
                        new TimeoutException($"{WorkerName} cycle timed out after {StepTimeout}"),
                        WorkerName, stoppingToken);
                }
                catch (Exception reportEx)
                {
                    _logger.LogWarning(reportEx, "{WorkerName} failed to report timeout", WorkerName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{WorkerName} error in cycle", WorkerName);

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reporter = scope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
                    await reporter.ReportErrorAsync(ex, WorkerName, stoppingToken);
                }
                catch (Exception reportEx)
                {
                    _logger.LogWarning(reportEx, "{WorkerName} failed to report error", WorkerName);
                }
            }

            // Wait for the next cycle, emitting idle keep-alive heartbeats so a long
            // PollInterval (e.g. the hourly / 6-hourly financial workers) never looks like a
            // hang to the watchdog. Liveness is decoupled from work cadence.
            if (!await DelayWithHeartbeatAsync(stoppingToken))
            {
                break;
            }
        }

        _logger.LogInformation("{WorkerName} stopped", WorkerName);
    }

    /// <summary>
    /// Maximum gap between liveness heartbeats while a worker is idle between cycles. Kept well
    /// under the watchdog's staleness thresholds so a worker with a long <see cref="PollInterval"/>
    /// still reports "alive" regularly (only a genuine hang then goes stale).
    /// </summary>
    protected virtual TimeSpan HeartbeatInterval => TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sleeps for <see cref="PollInterval"/>, broken into <see cref="HeartbeatInterval"/> slices,
    /// emitting an idle keep-alive heartbeat after each slice. Returns false if shutdown was
    /// requested during the wait (so the caller breaks the loop).
    /// </summary>
    private async Task<bool> DelayWithHeartbeatAsync(CancellationToken stoppingToken)
    {
        var remaining = PollInterval;
        while (remaining > TimeSpan.Zero)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return false;
            }

            var slice = remaining < HeartbeatInterval ? remaining : HeartbeatInterval;
            try
            {
                await Task.Delay(slice, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            remaining -= slice;
            if (remaining <= TimeSpan.Zero)
            {
                break; // next loop iteration runs the cycle, which emits its own heartbeat
            }

            // Idle keep-alive heartbeat (no work this tick). Best-effort: a failed write must
            // not break the wait loop.
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var reporter = scope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
                await reporter.ReportHeartbeatAsync(WorkerName, stoppingToken, processedDelta: 0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{WorkerName} failed to report idle heartbeat", WorkerName);
            }
        }

        return true;
    }

    /// <summary>
    /// Execute one cycle of work using a fresh DI scope. Return the number of items processed.
    /// </summary>
    protected abstract Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken);
}
