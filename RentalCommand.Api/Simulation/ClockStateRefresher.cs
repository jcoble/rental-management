using RentalCommand.Core.Time;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// Background service (non-production only) that polls <see cref="IClockStateProvider.RefreshAsync"/>
/// roughly once per second so the in-memory <see cref="ClockState"/> tracks the shared
/// <c>SimulationClock</c> row. Runs in both the API and Engine hosts. Transient DB errors are swallowed
/// and logged — the provider simply keeps serving the last-known state until the next successful tick.
/// </summary>
public sealed class ClockStateRefresher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IClockStateProvider _provider;
    private readonly ILogger<ClockStateRefresher> _logger;

    public ClockStateRefresher(IClockStateProvider provider, ILogger<ClockStateRefresher> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ClockStateRefresher starting (poll interval {Interval}).", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _provider.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Transient DB hiccup — keep serving the last-known clock state and retry next tick.
                _logger.LogWarning(ex, "SimulationClock refresh failed; keeping last-known clock state.");
            }

            try
            {
                // Delay on the REAL clock (TimeProvider.System), never the ambient sim clock — a frozen
                // simulation clock must not stall the refresher's own one-second cadence.
                await Task.Delay(PollInterval, TimeProvider.System, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
