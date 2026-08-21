using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Simulation;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Simulation;

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
public sealed class DevClockController : AuthenticatedPortfolioControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IClockStateProvider _clockState;
    private readonly IAppTimeZoneProvider _timeZoneProvider;
    private readonly IRequestWriteExecutor _writes;

    public DevClockController(
        TimeProvider timeProvider,
        IClockStateProvider clockState,
        IAppTimeZoneProvider timeZoneProvider,
        RentalCommandDbContext db,
        IRequestWriteExecutor writes)
    {
        _timeProvider = timeProvider;
        _clockState = clockState;
        _timeZoneProvider = timeZoneProvider;
        _db = db;
        _writes = writes;
    }

    /// <summary>Current simulated clock — in-memory only, so it works anonymously and pre-login.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ClockStateResponse), StatusCodes.Status200OK)]
    public ActionResult<ClockStateResponse> Get() => Ok(BuildResponse());

    /// <summary>Set the clock to a specific instant in <c>offset</c> (default) or <c>frozen</c> mode.</summary>
    [HttpPost("set")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Set(
        [FromBody] SetClockRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        DateTime instant;
        if (request.InstantUtc is { } instantUtc)
        {
            instant = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        }
        else if (!string.IsNullOrWhiteSpace(request.Date)
                 && DateOnly.TryParseExact(
                     request.Date,
                     "yyyy-MM-dd",
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out var businessDate))
        {
            TimeZoneInfo timeZone;
            try
            {
                timeZone = string.IsNullOrWhiteSpace(request.TimeZoneId)
                    ? _timeZoneProvider.BusinessTimeZone
                    : TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
            }
            catch (Exception exception) when (
                exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return BadRequest(new { error = $"Unknown or invalid timezone '{request.TimeZoneId}'." });
            }
            var localMidnight = DateTime.SpecifyKind(
                businessDate.ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Unspecified);
            instant = LocalDateTimeResolver.ConvertToUtc(localMidnight, timeZone);
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

        if (!TryBuildCommandContext(idempotencyKey, out var access, out var deliveryKey, out var failure))
            return failure;

        var frozen = string.Equals(request.Mode, "frozen", StringComparison.OrdinalIgnoreCase);
        var command = new SetSimulationClockCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            instant,
            frozen ? ClockMode.Frozen : ClockMode.Offset,
            request.TimeZoneId is not null,
            request.TimeZoneId);
        return await ExecuteClockCommandAsync(access, deliveryKey, command, ct);
    }

    /// <summary>Shift the simulated clock forward (or back, with negatives) by a delta.</summary>
    [HttpPost("advance")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Advance(
        [FromBody] AdvanceClockRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryBuildCommandContext(idempotencyKey, out var access, out var deliveryKey, out var failure))
            return failure;

        var command = new AdvanceSimulationClockCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            request.Days,
            request.Hours,
            request.Minutes,
            request.Seconds);
        return await ExecuteClockCommandAsync(access, deliveryKey, command, ct);
    }

    /// <summary>Freeze the clock at the current simulated instant.</summary>
    [HttpPost("freeze")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Freeze(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryBuildCommandContext(idempotencyKey, out var access, out var deliveryKey, out var failure))
            return failure;

        var command = new FreezeSimulationClockCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision);
        return await ExecuteClockCommandAsync(access, deliveryKey, command, ct);
    }

    /// <summary>Resume ticking from the currently-frozen instant (re-anchored Offset).</summary>
    [HttpPost("unfreeze")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Unfreeze(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryBuildCommandContext(idempotencyKey, out var access, out var deliveryKey, out var failure))
            return failure;

        var command = new UnfreezeSimulationClockCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision);
        return await ExecuteClockCommandAsync(access, deliveryKey, command, ct);
    }

    /// <summary>Return to real time (clears any timezone override).</summary>
    [HttpPost("reset")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    public async Task<ActionResult<ClockStateResponse>> Reset(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryBuildCommandContext(idempotencyKey, out var access, out var deliveryKey, out var failure))
            return failure;

        var command = new ResetSimulationClockCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision);
        return await ExecuteClockCommandAsync(access, deliveryKey, command, ct);
    }

    private async Task<ActionResult<ClockStateResponse>> ExecuteClockCommandAsync<TCommand>(
        ActiveAccessContext access,
        string deliveryKey,
        TCommand command,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        try
        {
            var outcome = await _writes.ExecuteExactAsync(
                BuildIdentityKey(access, deliveryKey),
                SimulationWriteSupport.Write<TCommand, SimulationClockMutationResult>(_db, command), ct);
            await _clockState.RefreshAsync(ct);
            return Ok(ToResponse(outcome.Value));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private bool TryBuildCommandContext(
        string? idempotencyKey,
        out ActiveAccessContext access,
        out string deliveryKey,
        out ActionResult<ClockStateResponse> failure)
    {
        access = null!;
        deliveryKey = string.Empty;
        failure = null!;
        if (!TryValidateIdempotencyKey(idempotencyKey, out deliveryKey))
        {
            failure = BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
            return false;
        }

        if (!TryGetActiveAccessContext(out access))
        {
            failure = Forbid();
            return false;
        }

        return true;
    }

    private ClockStateResponse BuildResponse()
    {
        var state = _clockState.Current;
        var simNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var realNowUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var offsetSeconds = Math.Round((simNowUtc - realNowUtc).TotalSeconds, 3);
        return new ClockStateResponse(simNowUtc, state.Mode.ToString(), state.TimeZoneId, offsetSeconds);
    }

    private static ClockStateResponse ToResponse(SimulationClockMutationResult result) =>
        new(result.SimNowUtc, result.Mode, result.TimeZoneId, result.OffsetSeconds);

    private static string BuildIdentityKey(ActiveAccessContext access, string idempotencyKey) =>
        $"{access.PortfolioId}:{access.UserId}:{idempotencyKey}";
}

/// <summary>Current simulated clock. <c>Mode</c> is "Real" | "Frozen" | "Offset".</summary>
public sealed record ClockStateResponse(DateTime SimNowUtc, string Mode, string? TimeZoneId, double OffsetSeconds);

/// <summary>Provide <c>InstantUtc</c> OR <c>Date</c>; <c>Mode</c> is "offset" (default) | "frozen".</summary>
public sealed record SetClockRequest(DateTime? InstantUtc, string? Date, string? TimeZoneId, string? Mode);

/// <summary>All fields optional (default 0); negatives move the clock backward.</summary>
public sealed record AdvanceClockRequest(int Days, int Hours, int Minutes, int Seconds);
