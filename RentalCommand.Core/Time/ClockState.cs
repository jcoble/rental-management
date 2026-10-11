namespace RentalCommand.Core.Time;

/// <summary>
/// Immutable in-memory snapshot of the <c>SimulationClock</c> row, held by
/// <see cref="IClockStateProvider"/> and read on every <c>GetUtcNow()</c> so the hot path never
/// touches the database.
/// </summary>
public sealed record ClockState(ClockMode Mode, DateTime SimAnchorUtc, DateTime RealAnchorUtc, string? TimeZoneId)
{
    /// <summary>Default before the first refresh / when simulation is disabled: pass-through real time.</summary>
    public static readonly ClockState RealTime = new(ClockMode.Real, default, default, null);
}
