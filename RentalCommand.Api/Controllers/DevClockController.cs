using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Simulation;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Dev-only control surface for the master simulation clock (spec §6.1). Mapped ONLY when simulation is
/// active (<see cref="SimulationOnlyAttribute"/> + <see cref="SimulationOnlyConvention"/>) — in production
/// the whole controller is unreachable (404) and never constructed.
///
/// <para><c>GET</c> is <see cref="AllowAnonymousAttribute">anonymous</see> (read-only; the web app syncs
/// the clock before login on register/confirm-email pages — <c>Simulation:Enabled</c> is the real gate),
/// and reads only in-memory state (no DB). Every mutation is admin-gated, persists the single
/// <c>SimulationClock</c> row (Id = 1), and forces an immediate <see cref="IClockStateProvider"/> refresh
/// so the next read reflects it.</para>
/// </summary>
[ApiController]
[Route("api/v1/dev/clock")]
[SimulationOnly]
[Produces("application/json")]
public sealed class DevClockController : ControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IClockStateProvider _clockState;

    public DevClockController(RentalCommandDbContext db, TimeProvider timeProvider, IClockStateProvider clockState)
    {
        _db = db;
        _timeProvider = timeProvider;
        _clockState = clockState;
    }

    /// <summary>Current simulated clock — in-memory only, so it works anonymously and pre-login.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ClockStateResponse), StatusCodes.Status200OK)]
    public ActionResult<ClockStateResponse> Get() => Ok(BuildResponse());

    /// <summary>Set the clock to a specific instant in <c>offset</c> (default) or <c>frozen</c> mode.</summary>
    [HttpPost("set")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Set([FromBody] SetClockRequest request, CancellationToken ct)
    {
        DateTime instant;
        if (request.InstantUtc is { } instantUtc)
        {
            instant = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        }
        else if (!string.IsNullOrWhiteSpace(request.Date)
                 && DateTime.TryParse(request.Date, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            instant = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        else
        {
            return BadRequest(new { error = "Provide 'instantUtc' or a parseable 'date' (e.g. \"2025-01-01\")." });
        }

        var frozen = string.Equals(request.Mode, "frozen", StringComparison.OrdinalIgnoreCase);
        var realNow = TimeProvider.System.GetUtcNow().UtcDateTime;

        var row = await LoadRowAsync(ct);
        row.Mode = frozen ? ClockMode.Frozen : ClockMode.Offset;
        row.SimAnchorUtc = instant;
        // Offset ticks forward from (RealAnchor now → SimAnchor instant); Frozen never ticks.
        row.RealAnchorUtc = frozen ? instant : realNow;
        if (request.TimeZoneId is not null)
            row.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? null : request.TimeZoneId;
        row.UpdatedAtRealUtc = realNow;

        return await SaveRefreshAndRespondAsync(ct);
    }

    /// <summary>Shift the simulated clock forward (or back, with negatives) by a delta.</summary>
    [HttpPost("advance")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Advance([FromBody] AdvanceClockRequest request, CancellationToken ct)
    {
        var delta = new TimeSpan(request.Days, request.Hours, request.Minutes, request.Seconds);
        var realNow = TimeProvider.System.GetUtcNow().UtcDateTime;

        var row = await LoadRowAsync(ct);
        if (row.Mode == ClockMode.Real)
        {
            // Advancing from real time re-anchors to Offset at "now" so the shift has a visible effect.
            row.Mode = ClockMode.Offset;
            row.RealAnchorUtc = realNow;
            row.SimAnchorUtc = realNow;
        }

        // Shifting the anchor advances sim-now by delta in both Frozen and Offset modes.
        row.SimAnchorUtc = row.SimAnchorUtc.Add(delta);
        row.UpdatedAtRealUtc = realNow;

        return await SaveRefreshAndRespondAsync(ct);
    }

    /// <summary>Freeze the clock at the current simulated instant.</summary>
    [HttpPost("freeze")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Freeze(CancellationToken ct)
    {
        var simNow = _timeProvider.GetUtcNow().UtcDateTime;
        var row = await LoadRowAsync(ct);
        row.Mode = ClockMode.Frozen;
        row.SimAnchorUtc = simNow;
        row.RealAnchorUtc = simNow;
        row.UpdatedAtRealUtc = TimeProvider.System.GetUtcNow().UtcDateTime;

        return await SaveRefreshAndRespondAsync(ct);
    }

    /// <summary>Resume ticking from the currently-frozen instant (re-anchored Offset).</summary>
    [HttpPost("unfreeze")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Unfreeze(CancellationToken ct)
    {
        var simNow = _timeProvider.GetUtcNow().UtcDateTime;
        var realNow = TimeProvider.System.GetUtcNow().UtcDateTime;
        var row = await LoadRowAsync(ct);
        row.Mode = ClockMode.Offset;
        row.SimAnchorUtc = simNow;
        row.RealAnchorUtc = realNow;
        row.UpdatedAtRealUtc = realNow;

        return await SaveRefreshAndRespondAsync(ct);
    }

    /// <summary>Return to real time (clears any timezone override).</summary>
    [HttpPost("reset")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Reset(CancellationToken ct)
    {
        var row = await LoadRowAsync(ct);
        row.Mode = ClockMode.Real;
        row.TimeZoneId = null;
        row.UpdatedAtRealUtc = TimeProvider.System.GetUtcNow().UtcDateTime;

        return await SaveRefreshAndRespondAsync(ct);
    }

    private async Task<SimulationClock> LoadRowAsync(CancellationToken ct)
    {
        var row = await _db.SimulationClocks.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (row is null)
        {
            // Defensive: the migration seeds row 1, but never NRE if it is somehow absent.
            row = new SimulationClock { Id = 1, Mode = ClockMode.Real };
            _db.SimulationClocks.Add(row);
        }
        return row;
    }

    private async Task<ActionResult<ClockStateResponse>> SaveRefreshAndRespondAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        await _clockState.RefreshAsync(ct);
        return Ok(BuildResponse());
    }

    private ClockStateResponse BuildResponse()
    {
        var state = _clockState.Current;
        var simNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var realNowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var offsetSeconds = Math.Round((simNowUtc - realNowUtc).TotalSeconds, 3);
        return new ClockStateResponse(simNowUtc, state.Mode.ToString(), state.TimeZoneId, offsetSeconds);
    }
}

/// <summary>Current simulated clock. <c>Mode</c> is "Real" | "Frozen" | "Offset".</summary>
public sealed record ClockStateResponse(DateTime SimNowUtc, string Mode, string? TimeZoneId, double OffsetSeconds);

/// <summary>Provide <c>InstantUtc</c> OR <c>Date</c>; <c>Mode</c> is "offset" (default) | "frozen".</summary>
public sealed record SetClockRequest(DateTime? InstantUtc, string? Date, string? TimeZoneId, string? Mode);

/// <summary>All fields optional (default 0); negatives move the clock backward.</summary>
public sealed record AdvanceClockRequest(int Days, int Hours, int Minutes, int Seconds);
