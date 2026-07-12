using System.Security.Claims;
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

    public UnitTurnoverController(IAtomicUnitOfWork atomic) => _atomic = atomic;

    [HttpPost("complete")]
    [ProducesResponseType(typeof(CompleteTurnoverResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(int unitId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CompleteTurnoverRequest request, CancellationToken ct)
    {
        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            return BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });
        }
        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)
            || !int.TryParse(User.FindFirstValue("ctx"), out var accessContextId)
            || !long.TryParse(User.FindFirstValue("ar"), out var accessRevision))
        {
            return Forbid();
        }

        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("unit.complete-turnover",
                    $"{portfolioId}:{unitId}:{request.TurnoverPeriodId}:{digest}"),
                new CompleteTurnoverCommand(portfolioId, unitId, request.TurnoverPeriodId, userId,
                    sessionId, accessContextId, accessRevision,
                    $"complete-turnover:{portfolioId}:{unitId}:{request.TurnoverPeriodId}:{digest}"),
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
