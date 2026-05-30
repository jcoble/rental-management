using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Base class for all engine workers (mirrors EdiPlatform.Engine.Workers.EngineWorkerBase).
/// Provides:
/// - A poll loop with a configurable interval.
/// - A fresh DI scope per cycle (so each cycle gets its own scoped DbContext + services).
/// - Per-cycle timeout via a linked <see cref="CancellationTokenSource"/>.
/// - Graceful shutdown on the host's stopping token.
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

    /// <summary>Human-readable worker name used in structured logs.</summary>
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();

                // Per-cycle timeout linked to the host's stopping token.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(StepTimeout);

                var processed = await ExecuteCycleAsync(scope.ServiceProvider, timeoutCts.Token);

                if (processed > 0)
                {
                    _logger.LogInformation("{WorkerName} processed {Count} item(s) this cycle", WorkerName, processed);
                }
                else
                {
                    _logger.LogDebug("{WorkerName} cycle completed (no items)", WorkerName);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is shutting down — exit the loop cleanly.
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-cycle timeout (not a shutdown). Log and continue to the next cycle.
                _logger.LogWarning("{WorkerName} cycle timed out after {Timeout}", WorkerName, StepTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{WorkerName} error in cycle", WorkerName);
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
