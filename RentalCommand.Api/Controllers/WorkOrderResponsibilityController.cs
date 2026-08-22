using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/work-orders/{workOrderId:int}/responsibilities")]
[Produces("application/json")]
public sealed class WorkOrderResponsibilityController : ManagementControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;
    private readonly WorkOrderResponsibilityAccessRevisionGuard _accessRevisionGuard;
    private readonly TimeProvider _timeProvider;

    public WorkOrderResponsibilityController(
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        WorkOrderResponsibilityAccessRevisionGuard accessRevisionGuard,
        TimeProvider timeProvider)
    {
        _db = db;
        _writes = writes;
        _accessRevisionGuard = accessRevisionGuard;
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
        var canManage = await _db.WorkOrders.AsNoTracking()
            .WhereAuthorized(_db, scope, [CapabilityKeys.ResponsibilityAssignExistingMember], now)
            .AnyAsync(workOrder => workOrder.Id == workOrderId, ct);
        var canReadAssigned = canManage || await _db.WorkOrders.AsNoTracking()
            .WhereAuthorized(_db, scope, [CapabilityKeys.AssignedWorkRead], now)
            .AnyAsync(workOrder => workOrder.Id == workOrderId, ct);
        if (!canReadAssigned)
            return NotFound();

        var rows = await _db.WorkOrderResponsibilities.AsNoTracking()
            .Where(responsibility =>
                responsibility.WorkOrderId == workOrderId &&
                responsibility.PortfolioId == scope.PortfolioId &&
                (canManage ? includeHistory || responsibility.EffectiveToUtc == null :
                    responsibility.EffectiveToUtc == null &&
                    responsibility.WorkspaceMembership!.AccessContextId == scope.AccessContextId))
            .OrderByDescending(responsibility => responsibility.EffectiveFromUtc)
            .ThenBy(responsibility => responsibility.Id)
            .Select(responsibility => new WorkOrderResponsibilityDto(
                responsibility.Id,
                responsibility.WorkOrderId,
                canManage ? responsibility.WorkspaceMembershipId : null,
                canManage ? responsibility.MembershipRoleAssignmentId : null,
                canManage ? responsibility.WorkspaceMembership!.AccessContextId : null,
                responsibility.WorkspaceMembership!.AccessContext!.User!.DisplayName,
                responsibility.MembershipRoleAssignment!.RoleProfile!.DisplayName,
                responsibility.Kind,
                responsibility.EffectiveFromUtc,
                responsibility.EffectiveToUtc,
                canManage ? responsibility.AssignedReason : null,
                canManage ? responsibility.AssignedAtUtc : null,
                canManage ? responsibility.EndedReason : null,
                canManage ? responsibility.EndedAtUtc : null,
                canManage))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpGet("candidates")]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponsibilityCandidateDto>>> Candidates(
        int workOrderId, [FromQuery] string? search = null, [FromQuery] int skip = 0,
        [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var scope = GetWorkspaceReadScope();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var term = search?.Trim();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);
        var authorizedWorkOrders = _db.WorkOrders.AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                new[] { CapabilityKeys.ResponsibilityAssignExistingMember },
                now);
        var rows = await _db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment => assignment.PortfolioId == scope.PortfolioId &&
                assignment.Status == RentalCommand.Core.Enums.MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= now &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
                assignment.ScopeKind == RentalCommand.Core.Enums.MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                assignment.RoleProfile!.Key == RoleProfileKeys.MaintenanceTechnician &&
                assignment.WorkspaceMembership!.Status == RentalCommand.Core.Enums.WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                (term == null || EF.Functions.ILike(assignment.WorkspaceMembership.AccessContext!.User!.DisplayName, $"%{term}%")) &&
                authorizedWorkOrders.Any(workOrder => workOrder.Id == workOrderId))
            .OrderBy(assignment => assignment.WorkspaceMembership!.AccessContext!.User!.DisplayName)
            .ThenBy(assignment => assignment.Id)
            .Skip(skip).Take(take)
            .Select(assignment => new WorkOrderResponsibilityCandidateDto(
                assignment.WorkspaceMembershipId, assignment.Id,
                assignment.WorkspaceMembership!.AccessContextId,
                assignment.WorkspaceMembership.AccessContext!.AccessRevision,
                assignment.WorkspaceMembership.AccessContext.User!.DisplayName,
                assignment.RoleProfile!.DisplayName))
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
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
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
            _timeProvider.GetUtcNow().UtcDateTime,
            digest);

        try
        {
            var operationKey = $"{active.PortfolioId}:{workOrderId}:{digest}";
            var outcome = await _writes.ExecuteAsync(operationKey,
                AssignWorkOrderResponsibilityRule.Write(command, _db, _accessRevisionGuard), ct);
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

    [HttpPost("{responsibilityId:guid}/close")]
    public async Task<IActionResult> Close(int workOrderId, Guid responsibilityId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CloseWorkOrderResponsibilityRequest request, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key)) return BadRequest();
        if (!TryGetActiveAccessContext(out var active)) return Forbid();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var command = new CloseWorkOrderResponsibilityCommand(active.PortfolioId, active.UserId,
            active.SessionId, active.AccessContextId, active.AccessRevision, workOrderId,
            responsibilityId, request.AccessRevisionExpectations.Select(item =>
                new WorkspaceAccessRevisionExpectation(item.AccessContextId, item.ExpectedRevision)).ToArray(),
            request.Reason, _timeProvider.GetUtcNow().UtcDateTime, digest);
        try
        {
            var operationKey = $"{active.PortfolioId}:{workOrderId}:{digest}";
            var outcome = await _writes.ExecuteAsync(operationKey,
                CloseWorkOrderResponsibilityRule.Write(command, _db, _accessRevisionGuard), ct);
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (StaleAccessRevisionException exception)
        {
            Response.Headers["X-Access-Envelope-Refresh"] = "required";
            return Conflict(new { error = exception.Message, exception.PresentedRevision, exception.CurrentRevision });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
}
