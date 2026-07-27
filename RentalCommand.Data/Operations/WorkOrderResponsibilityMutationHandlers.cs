using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class CloseWorkOrderResponsibilityHandler
    : IAtomicCommandHandler<CloseWorkOrderResponsibilityCommand, CloseWorkOrderResponsibilityResult>,
      IAtomicReplayAuthorizer<CloseWorkOrderResponsibilityCommand>
{
    public async Task<CloseWorkOrderResponsibilityResult> HandleAsync(
        CloseWorkOrderResponsibilityCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkOrder, command.WorkOrderId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.ActorAccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        await WorkOrderResponsibilityCommandAuthorization.AuthorizeManagerAsync(command.PortfolioId,
            command.ActorUserId, command.ActorAuthSessionId, command.ActorAccessContextId,
            command.ActorAccessRevision, command.WorkOrderId, attempt.Persistence, now, ct);

        var responsibility = await attempt.Persistence.Query<WorkOrderResponsibility>()
            .SingleOrDefaultAsync(item => item.Id == command.ResponsibilityId &&
                item.WorkOrderId == command.WorkOrderId && item.PortfolioId == command.PortfolioId &&
                item.EffectiveToUtc == null, ct)
            ?? throw new DomainValidationException("The responsibility is no longer current.");
        var affectedContextId = await attempt.Persistence.Query<WorkspaceMembership>().AsNoTracking()
            .Where(item => item.Id == responsibility.WorkspaceMembershipId &&
                           item.PortfolioId == command.PortfolioId)
            .Select(item => item.AccessContextId)
            .SingleAsync(ct);
        if (command.AccessRevisionExpectations.Length != 1 ||
            command.AccessRevisionExpectations[0].AccessContextId != affectedContextId)
            throw new DomainValidationException("The access revision expectation must identify the assignee.");

        if (affectedContextId != command.ActorAccessContextId)
            await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, affectedContextId, ct);
        var context = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .SingleOrDefaultAsync(item => item.Id == affectedContextId &&
                                          item.PortfolioId == command.PortfolioId, ct)
            ?? throw new AccessContextUnavailableException();
        var expected = command.AccessRevisionExpectations[0].ExpectedRevision;
        if (context.AccessRevision != expected)
            throw new StaleAccessRevisionException(expected, context.AccessRevision);
        context.UpdatedAtUtc = now;
        context.AdvanceRevision(expected);

        responsibility.EffectiveToUtc = now;
        responsibility.EndedAtUtc = now;
        responsibility.EndedByUserId = command.ActorUserId;
        responsibility.EndedByAccessContextId = command.ActorAccessContextId;
        responsibility.EndedReason = command.Reason.Trim();
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(WorkOrderResponsibility), command.WorkOrderId, AuditLogOperation.Updated,
            command.ActorUserId, NewValues: JsonSerializer.Serialize(new
            {
                command.ResponsibilityId,
                EffectiveToUtc = now,
            }), ChangeReason: command.Reason.Trim()), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = command.WorkOrderId }),
            IdempotencyKey = $"work-order-responsibility-close:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(command.ResponsibilityId, command.WorkOrderId, now,
            [new WorkspaceAccessRevisionExpectation(context.Id, context.AccessRevision)]);
    }

    public async Task AuthorizeReplayAsync(CloseWorkOrderResponsibilityCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        await WorkOrderResponsibilityCommandAuthorization.AuthorizeManagerAsync(command.PortfolioId,
            command.ActorUserId, command.ActorAuthSessionId, command.ActorAccessContextId,
            command.ActorAccessRevision, command.WorkOrderId, persistence, now, ct);
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
    : IAtomicCommandHandler<UpdateAssignedWorkOrderCommand, UpdateAssignedWorkOrderResult>,
      IAtomicReplayAuthorizer<UpdateAssignedWorkOrderCommand>
{
    public async Task<UpdateAssignedWorkOrderResult> HandleAsync(
        UpdateAssignedWorkOrderCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkOrder, command.WorkOrderId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.ActorAccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var workOrder = await AuthorizeAndLoadAsync(command, attempt.Persistence, now, tracking: true, ct);
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
        if (workOrder.Status == WorkOrderStatus.Completed) workOrder.CompletedAt ??= now;
        workOrder.UpdatedAt = now;

        var note = command.TechnicianNote?.Trim();
        if (workOrder.Status != fromStatus || note is not null)
            attempt.Persistence.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId,
                WorkOrderId = command.WorkOrderId,
                FromStatus = fromStatus,
                ToStatus = workOrder.Status,
                Note = note,
                ChangedByUserId = command.ActorUserId,
                ChangedByLabel = "Maintenance technician",
                CreatedAtUtc = now,
            });
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(WorkOrder),
            command.WorkOrderId, AuditLogOperation.Updated, command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                workOrder.Status,
                workOrder.ScheduledFor,
                workOrder.ScheduledWindowEnd,
                workOrder.CompletedAt,
                TechnicianNote = note,
            }), ChangeReason: "Assigned technician operational update"), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = command.WorkOrderId }),
            IdempotencyKey = $"assigned-work-order-update:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(UpdateAssignedWorkOrderOutcome.Applied, workOrder.Id, workOrder.Status, workOrder.ScheduledFor,
            workOrder.ScheduledWindowEnd, workOrder.CompletedAt, now);
    }

    public async Task AuthorizeReplayAsync(UpdateAssignedWorkOrderCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        _ = await AuthorizeAndLoadAsync(command, persistence, now, tracking: false, ct);
    }

    private static async Task<WorkOrder> AuthorizeAndLoadAsync(UpdateAssignedWorkOrderCommand command,
        IAtomicPersistenceSession persistence, DateTime now, bool tracking, CancellationToken ct)
    {
        var query = persistence.Query<WorkOrder>();
        if (!tracking) query = query.AsNoTracking();
        var result = await query
            .Where(item => item.Id == command.WorkOrderId && item.PortfolioId == command.PortfolioId)
            .Where(item =>
                persistence.Query<AuthSession>().Any(session =>
                    session.Id == command.ActorAuthSessionId &&
                    session.UserId == command.ActorUserId &&
                    session.ActiveAccessContextId == command.ActorAccessContextId &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > now) &&
                persistence.Query<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.ActorAccessContextId &&
                    context.UserId == command.ActorUserId &&
                    context.PortfolioId == item.PortfolioId &&
                    context.AccessRevision == command.ActorAccessRevision &&
                    context.Status == WorkspaceAccessContextStatus.Active &&
                    context.SuspendedAtUtc == null &&
                    context.RevokedAtUtc == null &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= now &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                        assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate &&
                            capability.CapabilityDefinition.AuthorizationTargetKind ==
                                CapabilityAuthorizationTargetKind.WorkOrder) &&
                        persistence.Query<WorkOrderResponsibility>().Any(responsibility =>
                            responsibility.WorkOrderId == item.Id &&
                            responsibility.PortfolioId == item.PortfolioId &&
                            responsibility.WorkspaceMembershipId == context.Membership.Id &&
                            responsibility.MembershipRoleAssignmentId == assignment.Id &&
                            responsibility.EffectiveFromUtc <= now &&
                            (responsibility.EffectiveToUtc == null || responsibility.EffectiveToUtc > now))))
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
        int actorContextId, long actorRevision, int workOrderId, IAtomicPersistenceSession persistence,
        DateTime now, CancellationToken ct)
    {
        var allowed = await persistence.Query<WorkOrder>()
            .AsNoTracking()
            .Where(item => item.Id == workOrderId && item.PortfolioId == portfolioId)
            .AnyAsync(
                item => persistence.Query<WorkspaceAccessContext>().AsNoTracking().Any(context =>
                    context.Id == actorContextId &&
                    context.UserId == actorUserId &&
                    context.PortfolioId == item.PortfolioId &&
                    context.AccessRevision == actorRevision &&
                    context.Status == WorkspaceAccessContextStatus.Active &&
                    context.SuspendedAtUtc == null &&
                    context.RevokedAtUtc == null &&
                    persistence.Query<AuthSession>().AsNoTracking().Any(session =>
                        session.Id == actorSessionId &&
                        session.UserId == actorUserId &&
                        session.ActiveAccessContextId == context.Id &&
                        session.Status == AuthSessionStatus.Active &&
                        session.RevokedAtUtc == null &&
                        session.ExpiresAtUtc > now) &&
                    context.Membership != null &&
                    context.Membership.Status == WorkspaceMembershipStatus.Active &&
                    context.Membership.RoleAssignments.Any(assignment =>
                        assignment.Status == MembershipRoleAssignmentStatus.Active &&
                        assignment.SuspendedAtUtc == null &&
                        assignment.RevokedAtUtc == null &&
                        assignment.EffectiveFromUtc <= now &&
                        (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
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
