using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/work-orders/{workOrderId:int}/responsibilities")]
[Produces("application/json")]
public sealed class WorkOrderResponsibilityController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<AssignWorkOrderResponsibilityResult> AssignCodec =
        new("work-order-responsibility.assign.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public WorkOrderResponsibilityController(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider)
    {
        _db = db;
        _atomic = atomic;
        _timeProvider = timeProvider;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WorkOrderResponsibilityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponsibilityDto>>> List(
        int workOrderId,
        [FromQuery] bool includeHistory = false,
        CancellationToken ct = default)
    {
        var scope = GetWorkspaceReadScope();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var rows = await _db.WorkOrderResponsibilities.AsNoTracking()
            .Where(responsibility =>
                responsibility.WorkOrderId == workOrderId &&
                responsibility.PortfolioId == scope.PortfolioId &&
                (includeHistory || responsibility.EffectiveToUtc == null) &&
                _db.WorkOrders.AsNoTracking()
                    .WhereAuthorized(
                        _db,
                        scope,
                        [CapabilityKeys.WorkRead, CapabilityKeys.AssignedWorkRead],
                        now)
                    .Any(workOrder =>
                        workOrder.Id == responsibility.WorkOrderId &&
                        workOrder.PortfolioId == responsibility.PortfolioId))
            .OrderByDescending(responsibility => responsibility.EffectiveFromUtc)
            .ThenBy(responsibility => responsibility.Id)
            .Select(responsibility => new WorkOrderResponsibilityDto(
                responsibility.Id,
                responsibility.WorkOrderId,
                responsibility.WorkspaceMembershipId,
                responsibility.MembershipRoleAssignmentId,
                responsibility.WorkspaceMembership!.AccessContextId,
                responsibility.WorkspaceMembership.AccessContext!.User!.DisplayName,
                responsibility.MembershipRoleAssignment!.RoleProfile!.DisplayName,
                responsibility.Kind,
                responsibility.EffectiveFromUtc,
                responsibility.EffectiveToUtc,
                responsibility.AssignedReason,
                responsibility.AssignedAtUtc,
                responsibility.EndedReason,
                responsibility.EndedAtUtc))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPut("current")]
    [ProducesResponseType(typeof(AssignWorkOrderResponsibilityResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(
        int workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] AssignWorkOrderResponsibilityRequest request,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var normalizedKey))
            return BadRequest(new { error = "A valid Idempotency-Key is required (maximum 128 characters)." });
        if (!TryGetActiveAccessContext(out var active))
            return Forbid();

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();
        var command = new AssignWorkOrderResponsibilityCommand(
            active.PortfolioId,
            active.UserId,
            active.SessionId,
            active.AccessContextId,
            active.AccessRevision,
            workOrderId,
            request.WorkspaceMembershipId,
            request.MembershipRoleAssignmentId,
            request.Kind,
            request.ExpectedCurrentPrimaryResponsibilityId,
            request.AccessRevisionExpectations
                .Select(item => new WorkspaceAccessRevisionExpectation(
                    item.AccessContextId,
                    item.ExpectedRevision))
                .ToArray(),
            request.Reason,
            digest);

        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "work-order-responsibility.assign",
                    $"{active.PortfolioId}:{workOrderId}:{digest}"),
                command,
                AssignCodec,
                ct);
            return Ok(new
            {
                outcome.Value,
                replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
            });
        }
        catch (StaleAccessRevisionException exception)
        {
            Response.Headers["X-Access-Envelope-Refresh"] = "required";
            return Conflict(new
            {
                error = exception.Message,
                exception.PresentedRevision,
                exception.CurrentRevision,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}
