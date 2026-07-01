using Microsoft.Extensions.Configuration;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Simulation;

/// <summary>
/// Resolves the business timezone used for day-boundary decisions (rent due dates, grace periods, late
/// fees, proration — spec §8). Precedence: the simulation override
/// (<see cref="IClockStateProvider"/>.<c>Current.TimeZoneId</c>) when set, else the configured
/// <c>App:TimeZone</c>, else <c>America/New_York</c> (the corpus ground truth).
///
/// <para>The <see cref="IClockStateProvider"/> dependency is optional so this provider also resolves when
/// simulation is disabled (production / config-only) — in that mode it reads config or the default and
/// never depends on the sim clock.</para>
/// </summary>
public sealed class AppTimeZoneProvider : IAppTimeZoneProvider
{
    private const string DefaultTimeZoneId = "America/New_York";

    private readonly IConfiguration _configuration;
    private readonly IClockStateProvider? _clockState;

    public AppTimeZoneProvider(IConfiguration configuration, IClockStateProvider? clockState = null)
    {
        _configuration = configuration;
        _clockState = clockState;
    }

    public TimeZoneInfo BusinessTimeZone
    {
        get
        {
            var id = _clockState?.Current.TimeZoneId;
            if (string.IsNullOrWhiteSpace(id))
                id = _configuration["App:TimeZone"];
            if (string.IsNullOrWhiteSpace(id))
                id = DefaultTimeZoneId;

            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
    }
}
