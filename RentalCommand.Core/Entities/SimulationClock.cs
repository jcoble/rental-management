using RentalCommand.Core.Time;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The single-row (Id = 1) source of truth for the controllable simulation clock. In non-production
/// it is read by both the API and Engine to compute "now" (see <see cref="ClockMode"/>). Never read
/// in production — there the injected provider is <c>TimeProvider.System</c> — so the row is harmless
/// if it exists unused. Global (no PortfolioId): it MUST be excluded from the tenant_isolation RLS
/// policy set so a portfolio-scoped session can still read it.
/// </summary>
public class SimulationClock
{
    public int Id { get; set; } = 1;

    public ClockMode Mode { get; set; } = ClockMode.Real;

    /// <summary>
    /// The simulated instant. In <see cref="ClockMode.Frozen"/> this is the fixed "now"; in
    /// <see cref="ClockMode.Offset"/> it is the anchor that pairs with <see cref="RealAnchorUtc"/>.
    /// </summary>
    public DateTime SimAnchorUtc { get; set; }

    /// <summary>The real instant when the anchor was set — used to tick forward in <see cref="ClockMode.Offset"/>.</summary>
    public DateTime RealAnchorUtc { get; set; }

    /// <summary>Optional business-timezone override (IANA id, e.g. "America/New_York") for day-boundary logic.</summary>
    public string? TimeZoneId { get; set; }

    public DateTime UpdatedAtRealUtc { get; set; }
}
