using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Operations;

public sealed class CloseWorkOrderResponsibilityHandler
    : IAtomicCommandHandler<CloseWorkOrderResponsibilityCommand, CloseWorkOrderResponsibilityResult>
{
    private readonly RentalCommandDbContext _db;
    private readonly WorkOrderResponsibilityAccessRevisionGuard _accessRevisionGuard;

    public CloseWorkOrderResponsibilityHandler(
        RentalCommandDbContext db,
        WorkOrderResponsibilityAccessRevisionGuard accessRevisionGuard)
    {
        _db = db;
        _accessRevisionGuard = accessRevisionGuard;
    }

    public async Task<CloseWorkOrderResponsibilityResult> HandleAsync(
        CloseWorkOrderResponsibilityCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var initiallyAffectedContextId = await _db.Set<WorkOrderResponsibility>()
            .AsNoTracking()
            .Where(item => item.Id == command.ResponsibilityId &&
                           item.WorkOrderId == command.WorkOrderId &&
                           item.PortfolioId == command.PortfolioId &&
                           item.EffectiveToUtc == null)
            .Select(item => (int?)item.WorkspaceMembership!.AccessContextId)
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("The responsibility is no longer current.");
        await WorkOrderProgressionLock.AcquireWorkspaceAccessContextsAsync(
            context, ct, command.ActorAccessContextId, initiallyAffectedContextId);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        await WorkOrderResponsibilityCommandAuthorization.AuthorizeManagerAsync(command.PortfolioId,
            command.ActorUserId, command.ActorAuthSessionId, command.ActorAccessContextId,
            command.ActorAccessRevision, command.WorkOrderId, _db, securityNowUtc, businessNowUtc, ct);

        var responsibility = await _db.Set<WorkOrderResponsibility>()
            .SingleOrDefaultAsync(item => item.Id == command.ResponsibilityId &&
                item.WorkOrderId == command.WorkOrderId && item.PortfolioId == command.PortfolioId &&
                item.EffectiveToUtc == null, ct)
            ?? throw new DomainValidationException("The responsibility is no longer current.");
        var affectedContextId = await _db.Set<WorkspaceMembership>().AsNoTracking()
            .Where(item => item.Id == responsibility.WorkspaceMembershipId &&
                           item.PortfolioId == command.PortfolioId)
            .Select(item => item.AccessContextId)
            .SingleAsync(ct);
        if (affectedContextId != initiallyAffectedContextId)
            throw new DomainValidationException("The responsibility context changed; refresh before retrying.", 409);
        if (command.AccessRevisionExpectations.Length != 1 ||
            command.AccessRevisionExpectations[0].AccessContextId != affectedContextId)
            throw new DomainValidationException("The access revision expectation must identify the assignee.");

        var accessContext = await _db.Set<WorkspaceAccessContext>()
            .SingleOrDefaultAsync(item => item.Id == affectedContextId &&
                                          item.PortfolioId == command.PortfolioId, ct)
            ?? throw new AccessContextUnavailableException();
        var expected = command.AccessRevisionExpectations[0].ExpectedRevision;
        if (accessContext.AccessRevision != expected)
            throw new StaleAccessRevisionException(expected, accessContext.AccessRevision);
        accessContext.UpdatedAtUtc = securityNowUtc;
        accessContext.AdvanceRevision(expected);

        responsibility.EffectiveToUtc = businessNowUtc;
        responsibility.EndedAtUtc = businessNowUtc;
        responsibility.EndedByUserId = command.ActorUserId;
        responsibility.EndedByAccessContextId = command.ActorAccessContextId;
        responsibility.EndedReason = command.Reason.Trim();
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(WorkOrderResponsibility), command.WorkOrderId, AuditLogOperation.Updated,
            command.ActorUserId, NewValues: JsonSerializer.Serialize(new
            {
                command.ResponsibilityId,
                EffectiveToUtc = businessNowUtc,
            }), ChangeReason: command.Reason.Trim()), businessNowUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = command.WorkOrderId }),
            IdempotencyKey = $"work-order-responsibility-close:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });
        await _accessRevisionGuard.ValidatePendingMutationAsync(
            _db, command.AccessRevisionExpectations, ct);
        return new(command.ResponsibilityId, command.WorkOrderId, businessNowUtc,
            [new WorkspaceAccessRevisionExpectation(accessContext.Id, accessContext.AccessRevision)]);
    }

    public async Task AuthorizeReplayAsync(CloseWorkOrderResponsibilityCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        await WorkOrderResponsibilityCommandAuthorization.AuthorizeManagerAsync(command.PortfolioId,
            command.ActorUserId, command.ActorAuthSessionId, command.ActorAccessContextId,
            command.ActorAccessRevision, command.WorkOrderId, _db, securityNowUtc, businessNowUtc, ct);
    }

    private static void Validate(CloseWorkOrderResponsibilityCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 || command.ActorAuthSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0 || command.WorkOrderId <= 0 ||
            command.ResponsibilityId == Guid.Empty || string.IsNullOrWhiteSpace(command.Reason) ||
            command.Reason.Trim().Length > 1000 || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A complete, valid responsibility close is required.");
    }
}

public sealed class UpdateAssignedWorkOrderHandler
    : IAtomicCommandHandler<UpdateAssignedWorkOrderCommand, UpdateAssignedWorkOrderResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateAssignedWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<UpdateAssignedWorkOrderResult> HandleAsync(
        UpdateAssignedWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync(
            "WorkspaceAccessContext", command.ActorAccessContextId, ct);
        await context.AcquireLockAsync("WorkOrder", command.WorkOrderId, ct);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var workOrder = await AuthorizeAndLoadAsync(
            command, _db, securityNowUtc, businessNowUtc, tracking: true, ct);
        if (workOrder.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
            throw new DomainValidationException(
                "Cancelled or archived work orders cannot be updated by a technician.", 409);
        if (workOrder.UpdatedAt != command.ExpectedUpdatedAtUtc)
            return new(UpdateAssignedWorkOrderOutcome.Stale, workOrder.Id, workOrder.Status,
                workOrder.ScheduledFor, workOrder.ScheduledWindowEnd, workOrder.CompletedAt, workOrder.UpdatedAt);
        var fromStatus = workOrder.Status;
        if (command.Status is WorkOrderStatus requested && requested != fromStatus)
        {
            if (!AllowedTransitions.TryGetValue(fromStatus, out var allowed) || !allowed.Contains(requested))
                throw new DomainValidationException(
                    $"A technician cannot move a work order from {fromStatus} to {requested}.");
        }

        if (command.ScheduledWindowEndUtc is not null && command.ScheduledForUtc is null && workOrder.ScheduledFor is null)
            throw new DomainValidationException("A schedule window end requires a schedule start.");
        var effectiveStart = command.ScheduledForUtc ?? workOrder.ScheduledFor;
        var effectiveEnd = command.ScheduledWindowEndUtc ?? workOrder.ScheduledWindowEnd;
        if (effectiveStart is not null && effectiveEnd is not null && effectiveEnd <= effectiveStart)
            throw new DomainValidationException("The schedule window must end after it starts.");
        var resultingStatus = command.Status ?? workOrder.Status;
        if (command.CompletedAtUtc is not null && resultingStatus != WorkOrderStatus.Completed)
            throw new DomainValidationException(
                "A completion time is only valid when the resulting status is Completed.");

        if (command.Status is WorkOrderStatus status) workOrder.Status = status;
        if (command.ScheduledForUtc is not null) workOrder.ScheduledFor = command.ScheduledForUtc;
        if (command.ScheduledWindowEndUtc is not null) workOrder.ScheduledWindowEnd = command.ScheduledWindowEndUtc;
        if (command.CompletedAtUtc is not null) workOrder.CompletedAt = command.CompletedAtUtc;
        if (workOrder.Status == WorkOrderStatus.Completed) workOrder.CompletedAt ??= businessNowUtc;
        workOrder.UpdatedAt = businessNowUtc;
        var eventNow = WorkOperationValidation.EventTimestamp(workOrder.RequestedAt, businessNowUtc);

        var note = command.TechnicianNote?.Trim();
        if (workOrder.Status != fromStatus || note is not null)
            _db.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId,
                WorkOrderId = command.WorkOrderId,
                FromStatus = fromStatus,
                ToStatus = workOrder.Status,
                Kind = note is null && workOrder.Status == fromStatus ? "Activity" : "Status",
                Visibility = "Public",
                Note = note,
                ChangedByUserId = command.ActorUserId,
                ChangedByLabel = "Maintenance technician",
                CreatedAtUtc = eventNow,
            });
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(WorkOrder),
            command.WorkOrderId, AuditLogOperation.Updated, command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                workOrder.Status,
                workOrder.ScheduledFor,
                workOrder.ScheduledWindowEnd,
                workOrder.CompletedAt,
                TechnicianNote = note,
            }), ChangeReason: "Assigned technician operational update"), businessNowUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = command.WorkOrderId }),
            IdempotencyKey = $"assigned-work-order-update:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });
        return new(UpdateAssignedWorkOrderOutcome.Applied, workOrder.Id, workOrder.Status, workOrder.ScheduledFor,
            workOrder.ScheduledWindowEnd, workOrder.CompletedAt, businessNowUtc);
    }

    public async Task AuthorizeReplayAsync(UpdateAssignedWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        _ = await AuthorizeAndLoadAsync(command, _db, securityNowUtc, businessNowUtc, tracking: false, ct);
    }

    private static async Task<WorkOrder> AuthorizeAndLoadAsync(UpdateAssignedWorkOrderCommand command,
        RentalCommandDbContext db, DateTime securityNowUtc, DateTime businessNowUtc, bool tracking,
        CancellationToken ct)
    {
        IQueryable<WorkOrder> query = db.Set<WorkOrder>();
        if (!tracking) query = query.AsNoTracking();
        var result = await query
            .Where(item => item.Id == command.WorkOrderId && item.PortfolioId == command.PortfolioId)
            .Where(item =>
                db.Set<AuthSession>().Any(session =>
                    session.Id == command.ActorAuthSessionId &&
                    session.UserId == command.ActorUserId &&
                    session.ActiveAccessContextId == command.ActorAccessContextId &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > securityNowUtc) &&
                db.Set<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.ActorAccessContextId &&
                    context.UserId == command.ActorUserId &&
                    context.PortfolioId == item.PortfolioId &&
                    context.AccessRevision == command.ActorAccessRevision &&
                    context.Status == WorkspaceAccessContextStatus.Active &&
                    context.SuspendedAtUtc == null &&
                    context.RevokedAtUtc == null &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.EffectiveFromUtc <= businessNowUtc &&
                    (context.Membership.EffectiveToUtc == null ||
                     context.Membership.EffectiveToUtc > businessNowUtc) &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= businessNowUtc &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNowUtc) &&
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                        assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate &&
                            capability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.WorkOrder) &&
                        db.Set<WorkOrderResponsibility>().Any(responsibility =>
                            responsibility.WorkOrderId == item.Id &&
                            responsibility.PortfolioId == item.PortfolioId &&
                            responsibility.WorkspaceMembershipId == context.Membership.Id &&
                            responsibility.MembershipRoleAssignmentId == assignment.Id &&
                            responsibility.EffectiveFromUtc <= businessNowUtc &&
                            (responsibility.EffectiveToUtc == null || responsibility.EffectiveToUtc > businessNowUtc))))
            )
            .SingleOrDefaultAsync(ct);
        return result ?? throw new UnauthorizedAccessException("The technician is not currently assigned to this work order.");
    }

    private static readonly IReadOnlyDictionary<WorkOrderStatus, WorkOrderStatus[]> AllowedTransitions =
        new Dictionary<WorkOrderStatus, WorkOrderStatus[]>
        {
            [WorkOrderStatus.New] = [WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold, WorkOrderStatus.Cancelled, WorkOrderStatus.Completed],
            [WorkOrderStatus.Scheduled] = [WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold, WorkOrderStatus.Cancelled, WorkOrderStatus.Completed],
            [WorkOrderStatus.InProgress] = [WorkOrderStatus.WaitingParts, WorkOrderStatus.OnHold, WorkOrderStatus.Completed],
            [WorkOrderStatus.WaitingParts] = [WorkOrderStatus.InProgress, WorkOrderStatus.OnHold, WorkOrderStatus.Completed],
            [WorkOrderStatus.OnHold] = [WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.WaitingParts, WorkOrderStatus.Cancelled, WorkOrderStatus.Completed],
            [WorkOrderStatus.Completed] = [], [WorkOrderStatus.Cancelled] = [], [WorkOrderStatus.Archived] = [],
        };

    private static void Validate(UpdateAssignedWorkOrderCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 || command.ActorAuthSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0 || command.WorkOrderId <= 0 ||
            command.ExpectedUpdatedAtUtc == default ||
            (command.Status is null && string.IsNullOrWhiteSpace(command.TechnicianNote) &&
             command.ScheduledForUtc is null && command.ScheduledWindowEndUtc is null && command.CompletedAtUtc is null) ||
            command.TechnicianNote?.Trim().Length > 2000 || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("At least one valid technician update is required.");
    }
}

internal static class WorkOrderResponsibilityCommandAuthorization
{
    internal static async Task AuthorizeManagerAsync(int portfolioId, int actorUserId, Guid actorSessionId,
        int actorContextId, long actorRevision, int workOrderId, RentalCommandDbContext db,
        DateTime securityNowUtc, DateTime businessNowUtc, CancellationToken ct)
    {
        var allowed = await db.Set<WorkOrder>()
            .AsNoTracking()
            .Where(item => item.Id == workOrderId && item.PortfolioId == portfolioId)
            .AnyAsync(
                item => db.Set<WorkspaceAccessContext>().AsNoTracking().Any(context =>
                    context.Id == actorContextId &&
                    context.UserId == actorUserId &&
                    context.PortfolioId == item.PortfolioId &&
                    context.AccessRevision == actorRevision &&
                    context.Status == WorkspaceAccessContextStatus.Active &&
                    context.SuspendedAtUtc == null &&
                    context.RevokedAtUtc == null &&
                    db.Set<AuthSession>().AsNoTracking().Any(session =>
                        session.Id == actorSessionId &&
                        session.UserId == actorUserId &&
                        session.ActiveAccessContextId == context.Id &&
                        session.Status == AuthSessionStatus.Active &&
                        session.RevokedAtUtc == null &&
                        session.ExpiresAtUtc > securityNowUtc) &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.EffectiveFromUtc <= businessNowUtc &&
                    (context.Membership.EffectiveToUtc == null ||
                     context.Membership.EffectiveToUtc > businessNowUtc) &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= businessNowUtc &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNowUtc) &&
                        assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.ResponsibilityAssignExistingMember &&
                            capability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.Property) &&
                        (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                         (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                          assignment.SelectedProperties.Any(selected =>
                              selected.PropertyId == item.PropertyId &&
                              selected.PortfolioId == item.PortfolioId))))),
                ct);
        if (!allowed) throw new UnauthorizedAccessException("The active assignment cannot manage this responsibility.");
    }
}
