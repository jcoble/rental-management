namespace RentalCommand.Core.Time;

/// <summary>
/// Holds the current <see cref="ClockState"/> in memory for cheap, synchronous reads (used by the
/// simulation <c>TimeProvider</c> on every <c>GetUtcNow()</c>) and refreshes it from the
/// <c>SimulationClock</c> row. A background refresher polls <see cref="RefreshAsync"/> roughly once
/// per second in both the API and Engine processes so they agree on "now".
/// </summary>
public interface IClockStateProvider
{
    /// <summary>The latest known clock state (in-memory; never blocks on the DB).</summary>
    ClockState Current { get; }

    /// <summary>True once <see cref="Current"/> has been loaded from the persisted row at least once.</summary>
    bool HasLoadedPersistedState { get; }

    /// <summary>Re-read the <c>SimulationClock</c> row into <see cref="Current"/>.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Load the persisted clock state if this provider has not completed its first refresh.</summary>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
}
