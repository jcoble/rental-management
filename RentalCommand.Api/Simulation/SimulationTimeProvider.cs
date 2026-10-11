using RentalCommand.Core.Time;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// The ambient <see cref="TimeProvider"/> in non-production: computes "now" from the in-memory
/// <see cref="ClockState"/> (see spec §5.2) instead of the machine clock. In production the DI-bound
/// provider is <see cref="TimeProvider.System"/> and this type is never registered.
/// <list type="bullet">
/// <item><see cref="ClockMode.Real"/> — pass through to the real clock (<c>base.GetUtcNow()</c>).</item>
/// <item><see cref="ClockMode.Frozen"/> — a fixed instant (<c>SimAnchorUtc</c>).</item>
/// <item><see cref="ClockMode.Offset"/> — the real clock shifted by <c>SimAnchorUtc - RealAnchorUtc</c>.</item>
/// </list>
/// <c>GetTimestamp()</c>/<c>CreateTimer()</c> are intentionally NOT overridden — stopwatch timing and
/// timers must stay real (the refresher's own delay uses <see cref="TimeProvider.System"/> anyway).
/// </summary>
public sealed class SimulationTimeProvider : TimeProvider
{
    private readonly IClockStateProvider _state;

    public SimulationTimeProvider(IClockStateProvider state) => _state = state;

    public override DateTimeOffset GetUtcNow()
    {
        var state = _state.Current;
        return state.Mode switch
        {
            // SpecifyKind(Utc) guards against a non-UTC-Kind anchor being read as local time when it is
            // widened to DateTimeOffset (anchors are always stored UTC, but this keeps the offset at +00).
            ClockMode.Frozen => new DateTimeOffset(DateTime.SpecifyKind(state.SimAnchorUtc, DateTimeKind.Utc)),
            ClockMode.Offset => base.GetUtcNow() + (state.SimAnchorUtc - state.RealAnchorUtc),
            _ => base.GetUtcNow(),
        };
    }

    public override TimeZoneInfo LocalTimeZone
    {
        get
        {
            // NOTE: this is NOT the business-day lever — day-boundary logic reads IAppTimeZoneProvider
            // (spec §8). This just keeps TimeProvider.LocalTimeZone consistent when a zone is simulated.
            var timeZoneId = _state.Current.TimeZoneId;
            return string.IsNullOrWhiteSpace(timeZoneId)
                ? base.LocalTimeZone
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
    }
}
