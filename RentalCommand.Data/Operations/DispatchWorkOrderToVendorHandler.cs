using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Operations;

public sealed class DispatchWorkOrderToVendorHandler
    : IAtomicCommandHandler<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult>,
      IAtomicReplayAuthorizer<DispatchWorkOrderToVendorCommand>
{
    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public async Task<DispatchWorkOrderToVendorResult> HandleAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkOrder, command.WorkOrderId, ct);
        var workOrders = attempt.Persistence.Query<WorkOrder>()
            .Where(candidate => candidate.Id == command.WorkOrderId
                && candidate.PortfolioId == command.PortfolioId);
        if (command.ManagementAccess is not null)
        {
            var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
            workOrders = WhereManagementAuthorized(
                workOrders, attempt.Persistence, command, now);
        }

        var workOrder = await workOrders.SingleOrDefaultAsync(ct);
        var vendorExists = await attempt.Persistence.Query<Vendor>()
            .AnyAsync(candidate => candidate.Id == command.VendorId
                && candidate.PortfolioId == command.PortfolioId, ct);
        if (workOrder is null || !vendorExists)
        {
            return Empty(DispatchWorkOrderToVendorOutcome.NotFound, command);
        }

        var alreadyOpen = await attempt.Persistence.Query<VendorDispatch>()
            .AnyAsync(dispatch => dispatch.PortfolioId == command.PortfolioId
                && dispatch.WorkOrderId == command.WorkOrderId
                && dispatch.VendorId == command.VendorId
                && OpenStatuses.Contains(dispatch.Status), ct);
        if (alreadyOpen)
        {
            return Empty(DispatchWorkOrderToVendorOutcome.AlreadyDispatched, command);
        }

        workOrder.VendorId = command.VendorId;
        workOrder.UpdatedAt = command.DispatchedAtUtc;
        attempt.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(WorkOrder),
            workOrder.Id,
            AuditLogOperation.Updated,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
            NewValues: JsonSerializer.Serialize(new { workOrder.VendorId }),
            ChangeReason: $"Work order #{workOrder.Id} assigned for vendor dispatch."));

        var dispatch = new VendorDispatch
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = command.WorkOrderId,
            VendorId = command.VendorId,
            Status = VendorDispatchStatus.Dispatched,
            DispatchedAtUtc = command.DispatchedAtUtc,
            Message = command.Message,
        };
        attempt.Persistence.Add(dispatch);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(VendorDispatch),
            dispatch.Id,
            AuditLogOperation.Created,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
            NewValues: JsonSerializer.Serialize(new
            {
                dispatch.WorkOrderId,
                dispatch.VendorId,
                Status = dispatch.Status.ToString(),
            }),
            ChangeReason: $"Work order #{workOrder.Id} dispatched to vendor #{command.VendorId} by SMS."));
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "sms",
            Payload = JsonSerializer.Serialize(new { to = command.DestinationPhone, message = command.Message }),
            IdempotencyKey = $"vendor-dispatch:{dispatch.Id}:sms",
            CreatedAtUtc = command.DispatchedAtUtc,
            NextAttemptAtUtc = command.DispatchedAtUtc,
        });

        return new DispatchWorkOrderToVendorResult(
            DispatchWorkOrderToVendorOutcome.Dispatched,
            dispatch.Id,
            dispatch.PortfolioId,
            dispatch.WorkOrderId,
            dispatch.VendorId,
            dispatch.Status,
            dispatch.DispatchedAtUtc,
            dispatch.Message);
    }

    public async Task AuthorizeReplayAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (command.ManagementAccess is null)
        {
            return;
        }

        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var authorized = await WhereManagementAuthorized(
                persistence.Query<WorkOrder>().Where(workOrder =>
                    workOrder.Id == command.WorkOrderId &&
                    workOrder.PortfolioId == command.PortfolioId),
                persistence,
                command,
                now)
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The current workspace access no longer authorizes this work-order dispatch.");
        }
    }

    private static IQueryable<WorkOrder> WhereManagementAuthorized(
        IQueryable<WorkOrder> workOrders,
        IAtomicPersistenceSession persistence,
        DispatchWorkOrderToVendorCommand command,
        DateTime utcNow)
    {
        var access = command.ManagementAccess
            ?? throw new InvalidOperationException("Management access is required for this query.");
        return workOrders.Where(workOrder =>
            persistence.Query<AuthSession>().Any(session =>
                session.Id == access.SessionId &&
                session.UserId == access.UserId &&
                session.ActiveAccessContextId == access.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.Id == access.AccessContextId &&
                session.ActiveAccessContext.UserId == access.UserId &&
                session.ActiveAccessContext.PortfolioId == command.PortfolioId &&
                session.ActiveAccessContext.AccessRevision == access.AccessRevision &&
                session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.SuspendedAtUtc == null &&
                session.ActiveAccessContext.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.PortfolioId == command.PortfolioId &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == command.PortfolioId &&
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null &&
                    assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= utcNow &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                    assignment.RoleProfile != null &&
                    assignment.RoleProfile.Capabilities.Any(profileCapability =>
                        profileCapability.CapabilityDefinition != null &&
                        profileCapability.CapabilityDefinition.Key == CapabilityKeys.WorkManage &&
                        profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PortfolioId == command.PortfolioId &&
                          selected.PropertyId == workOrder.PropertyId))))));
    }

    private static DispatchWorkOrderToVendorResult Empty(
        DispatchWorkOrderToVendorOutcome outcome,
        DispatchWorkOrderToVendorCommand command) =>
        new(outcome, 0, command.PortfolioId, command.WorkOrderId, command.VendorId,
            VendorDispatchStatus.Dispatched, command.DispatchedAtUtc, null);
}
