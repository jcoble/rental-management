using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/units/{unitId:int}/turnover")]
[Produces("application/json")]
public sealed class UnitTurnoverController : ManagementControllerBase
{
    private readonly IRequestWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public UnitTurnoverController(
        IRequestWriteExecutor writes,
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _writes = writes;
        _db = db;
        _timeProvider = timeProvider;
    }

    [HttpPost("{periodId:int}/complete")]
    [ProducesResponseType(typeof(CompleteTurnoverResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(int unitId, int periodId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            return BadRequest(new { error = "A request key is required and cannot exceed 200 characters." });
        }
        if (!TryGetActiveAccessContext(out var active))
        {
            return Forbid();
        }

        var portfolioId = active.PortfolioId;
        var userId = active.UserId;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();
        var businessNowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            var command = new CompleteTurnoverCommand(
                    portfolioId, unitId, periodId, userId,
                    active.SessionId, active.AccessContextId, active.AccessRevision, businessNowUtc,
                    $"complete-turnover:{portfolioId}:{unitId}:{periodId}:{digest}");
            var outcome = await _writes.ExecuteAsync(
                $"{portfolioId}:{unitId}:{periodId}:{digest}",
                LeasingWriteSupport.Write<CompleteTurnoverCommand, CompleteTurnoverResult>(_db, command), ct);
            return outcome.Value.Outcome switch
            {
                CompleteTurnoverOutcome.Completed or CompleteTurnoverOutcome.AlreadyCompleted
                    when outcome.Value.CompletedAtUtc.HasValue => Ok(new CompleteTurnoverResponse(
                        outcome.Value.UnitId, outcome.Value.TurnoverPeriodId,
                        outcome.Value.CompletedAtUtc.Value,
                        outcome.Disposition != AtomicCommandDisposition.Executed)),
                CompleteTurnoverOutcome.TurnoverNotFound => NotFound(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }
}
