using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// Holds the current <see cref="ClockState"/> in memory so the simulation <c>TimeProvider</c> can read
/// "now" cheaply and synchronously on every <c>GetUtcNow()</c> without touching the database.
/// <see cref="RefreshAsync"/> re-reads the single <c>SimulationClock</c> row (Id = 1) through a freshly
/// scoped <see cref="RentalCommandDbContext"/>; the <see cref="ClockStateRefresher"/> background service
/// calls it roughly once per second in both the API and Engine so the two processes agree on "now".
/// Registered as a singleton (non-production only) — the hot read path is the ambient
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed class ClockStateProvider : IClockStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    // Reference assignment is atomic and ClockState is an immutable record, so the hot Current read
    // never needs a lock; `volatile` publishes a freshly-refreshed snapshot to reader threads at once.
    private volatile ClockState _current = ClockState.RealTime;
    private volatile bool _hasLoadedPersistedState;

    public ClockStateProvider(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <inheritdoc />
    public ClockState Current => _current;

    /// <inheritdoc />
    public bool HasLoadedPersistedState => _hasLoadedPersistedState;

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        // A singleton cannot hold a scoped DbContext, so open a short-lived scope per poll.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

        var row = await db.SimulationClocks.FindAsync(new object?[] { 1 }, cancellationToken);
        if (row is null)
        {
            // No seed row (should not happen once the migration has run) — keep the last-known state
            // rather than snapping back to real time.
            return;
        }

        _current = new ClockState(row.Mode, row.SimAnchorUtc, row.RealAnchorUtc, row.TimeZoneId);
        _hasLoadedPersistedState = true;
    }

    /// <inheritdoc />
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_hasLoadedPersistedState)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_hasLoadedPersistedState)
            {
                return;
            }

            await RefreshAsync(cancellationToken);

            if (!_hasLoadedPersistedState)
            {
                throw new InvalidOperationException("SimulationClock row 1 was not found; refusing to use the default real clock as an initialized simulation state.");
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }
}
