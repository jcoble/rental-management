namespace RentalCommand.Core.Time;

/// <summary>
/// The business timezone used for day-boundary decisions (rent due dates, grace periods, late fees,
/// proration). Returns the simulation override (<c>SimulationClock.TimeZoneId</c>) when set, else the
/// configured <c>App:TimeZone</c>, else a sensible default. This is the single seam so the simulation
/// clock, the grid date-range filters, and the proration primitive all compute the same day boundaries.
/// </summary>
public interface IAppTimeZoneProvider
{
    TimeZoneInfo BusinessTimeZone { get; }
}
