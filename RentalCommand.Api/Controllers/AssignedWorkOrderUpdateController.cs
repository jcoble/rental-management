using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Operations;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/work-orders/{workOrderId:int}/assigned-update")]
public sealed class AssignedWorkOrderUpdateController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<UpdateAssignedWorkOrderResult> Codec =
        new("assigned-work-order.update.v1");
    private readonly IAtomicUnitOfWork _atomic;

    public AssignedWorkOrderUpdateController(IAtomicUnitOfWork atomic) => _atomic = atomic;

    [HttpPatch]
    public async Task<IActionResult> Update(int workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] UpdateAssignedWorkOrderRequest request, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key)) return BadRequest();
        if (!TryGetActiveAccessContext(out var active)) return Forbid();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var command = new UpdateAssignedWorkOrderCommand(active.PortfolioId, active.UserId,
            active.SessionId, active.AccessContextId, active.AccessRevision, workOrderId,
            request.ExpectedUpdatedAtUtc.ToUniversalTime(), request.Status,
            request.TechnicianNote, request.ScheduledFor?.UtcDateTime,
            request.ScheduledWindowEnd?.UtcDateTime, request.CompletedAt?.UtcDateTime, digest);
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(
                "assigned-work-order.update", $"{active.PortfolioId}:{workOrderId}:{digest}"), command, Codec, ct);
            return outcome.Value.Outcome == UpdateAssignedWorkOrderOutcome.Stale
                ? Conflict(new { error = "The work order changed; refresh before retrying.", outcome.Value,
                    replayed = outcome.Disposition == AtomicCommandDisposition.Replayed })
                : Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}
