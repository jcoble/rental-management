using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/units/{unitId:int}/turnover")]
[Produces("application/json")]
public sealed class UnitTurnoverController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<CompleteTurnoverResult> ResultCodec =
        new("unit.complete-turnover.v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public UnitTurnoverController(IAtomicUnitOfWork atomic, TimeProvider timeProvider)
    {
        _atomic = atomic;
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
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("unit.complete-turnover",
                    $"{portfolioId}:{unitId}:{periodId}:{digest}"),
                new CompleteTurnoverCommand(portfolioId, unitId, periodId, userId,
                    active.SessionId, active.AccessContextId, active.AccessRevision, businessNowUtc,
                    $"complete-turnover:{portfolioId}:{unitId}:{periodId}:{digest}"),
                ResultCodec, ct);
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
