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

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("{WorkerName} stopped", WorkerName);
    }

    /// <summary>
    /// Execute one cycle of work using a fresh DI scope. Return the number of items processed.
    /// </summary>
    protected abstract Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken);
}
