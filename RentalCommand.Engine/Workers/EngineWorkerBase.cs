using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Time;
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
/// - Per-worker restart with bounded exponential backoff after a failed cycle.
///
/// Single-instance safety (the PostgreSQL advisory lock) is enforced once at host
/// startup in Program.cs; the lock is released on graceful shutdown there. Workers
/// therefore assume they are the only Engine running against the database.
/// </summary>
public abstract class EngineWorkerBase : BackgroundService
{
    private static readonly TimeSpan InitialFailureBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumFailureBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureBackoffResetPeriod = TimeSpan.FromMinutes(5);

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

        if (!await WaitForSimulationClockStartupAsync(stoppingToken))
        {
            _logger.LogInformation("{WorkerName} stopped before simulation clock startup completed", WorkerName);
            return;
        }

        var consecutiveFailures = 0;
        DateTimeOffset? lastFailureAt = null;
        var succeededSinceLastFailure = false;

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
            TimeSpan? restartDelay = null;

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
                succeededSinceLastFailure = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-cycle timeout (not a shutdown). Log, report, and restart this worker.
                restartDelay = RegisterFailure(
                    ref consecutiveFailures,
                    ref lastFailureAt,
                    ref succeededSinceLastFailure);
                _logger.LogWarning(
                    "{WorkerName} cycle timed out after {Timeout}; restarting this worker in {RestartDelay}",
                    WorkerName,
                    StepTimeout,
                    restartDelay);

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
                restartDelay = RegisterFailure(
                    ref consecutiveFailures,
                    ref lastFailureAt,
                    ref succeededSinceLastFailure);
                _logger.LogError(
                    ex,
                    "{WorkerName} failed; restarting this worker in {RestartDelay}",
                    WorkerName,
                    restartDelay);

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

            if (restartDelay is { } delay)
            {
                try
                {
                    await Task.Delay(delay, TimeProvider.System, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
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

    private static TimeSpan RegisterFailure(
        ref int consecutiveFailures,
        ref DateTimeOffset? lastFailureAt,
        ref bool succeededSinceLastFailure)
    {
        var now = TimeProvider.System.GetUtcNow();
        if (lastFailureAt is { } previousFailure
            && succeededSinceLastFailure
            && now - previousFailure >= FailureBackoffResetPeriod)
        {
            consecutiveFailures = 0;
        }

        consecutiveFailures = Math.Min(consecutiveFailures + 1, 7);
        lastFailureAt = now;
        succeededSinceLastFailure = false;

        var multiplier = 1 << (consecutiveFailures - 1);
        var delay = InitialFailureBackoff * multiplier;
        return delay <= MaximumFailureBackoff ? delay : MaximumFailureBackoff;
    }

    /// <summary>
    /// Maximum gap between liveness heartbeats while a worker is idle between cycles. Kept well
    /// under the watchdog's staleness thresholds so a worker with a long <see cref="PollInterval"/>
    /// still reports "alive" regularly (only a genuine hang then goes stale).
    /// </summary>
    protected virtual TimeSpan HeartbeatInterval => TimeSpan.FromMinutes(2);

    /// <summary>
    /// In simulation-enabled hosts, the ambient <see cref="TimeProvider"/> starts with a safe default
    /// real-clock state until the persisted <c>SimulationClock</c> row is loaded. Engine workers mutate
    /// durable rows, so they must not run a cycle until that first persisted state is authoritative.
    /// </summary>
    private async Task<bool> WaitForSimulationClockStartupAsync(CancellationToken stoppingToken)
    {
        IClockStateProvider? clockState;
        try
        {
            clockState = _serviceProvider.GetService<IClockStateProvider>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{WorkerName} failed to resolve simulation clock state provider", WorkerName);
            return false;
        }

        if (clockState is null || clockState.HasLoadedPersistedState)
        {
            return true;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await clockState.EnsureInitializedAsync(stoppingToken);
                _logger.LogInformation("{WorkerName} loaded persisted simulation clock state before first cycle", WorkerName);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{WorkerName} is waiting for persisted simulation clock state before processing", WorkerName);

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reporter = scope.ServiceProvider.GetRequiredService<EngineStatusReporter>();
                    await reporter.ReportErrorAsync(ex, WorkerName, stoppingToken);
                }
                catch (Exception reportEx)
                {
                    _logger.LogWarning(reportEx, "{WorkerName} failed to report simulation clock startup error", WorkerName);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

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
