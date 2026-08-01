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
    : IAtomicCommandHandler<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult>
{
    private readonly RentalCommandDbContext _db;

    public DispatchWorkOrderToVendorHandler(RentalCommandDbContext db) => _db = db;

    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public async Task<DispatchWorkOrderToVendorResult> HandleAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("WorkOrder", command.WorkOrderId, ct);
        var workOrders = _db.Set<WorkOrder>()
            .Where(candidate => candidate.Id == command.WorkOrderId
                && candidate.PortfolioId == command.PortfolioId);
        if (command.ManagementAccess is not null)
        {
            var now = await context.ReadDatabaseClockUtcAsync(ct);
            workOrders = WhereManagementAuthorized(
                workOrders,
                _db,
                command.PortfolioId,
                command.ManagementAccess,
                now);
        }

        var workOrder = await workOrders.SingleOrDefaultAsync(ct);
        var vendorExists = await _db.Set<Vendor>()
            .AnyAsync(candidate => candidate.Id == command.VendorId
                && candidate.PortfolioId == command.PortfolioId, ct);
        if (workOrder is null || !vendorExists)
        {
            return Empty(DispatchWorkOrderToVendorOutcome.NotFound, command);
        }

        var alreadyOpen = await _db.Set<VendorDispatch>()
            .AnyAsync(dispatch => dispatch.PortfolioId == command.PortfolioId
                && dispatch.WorkOrderId == command.WorkOrderId
                && dispatch.VendorId == command.VendorId
                && OpenStatuses.Contains(dispatch.Status), ct);
        if (alreadyOpen)
        {
            return Empty(DispatchWorkOrderToVendorOutcome.AlreadyDispatched, command);
        }

        var workOrderChanged =
            workOrder.VendorId != command.VendorId ||
            workOrder.UpdatedAt != command.DispatchedAtUtc;
        workOrder.VendorId = command.VendorId;
        workOrder.UpdatedAt = command.DispatchedAtUtc;
        if (workOrderChanged)
        {
            context.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(WorkOrder),
                workOrder.Id,
                AuditLogOperation.Updated,
                UserId: command.ChangedByUserId,
                ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
                NewValues: JsonSerializer.Serialize(new { workOrder.VendorId }),
                ChangeReason: $"Work order #{workOrder.Id} assigned for vendor dispatch."));
        }

        var dispatch = new VendorDispatch
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = command.WorkOrderId,
            VendorId = command.VendorId,
            Status = VendorDispatchStatus.Dispatched,
            DispatchedAtUtc = command.DispatchedAtUtc,
            Message = command.Message,
        };
        _db.Add(dispatch);
        var statusEvent = new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = workOrder.Id,
            FromStatus = workOrder.Status,
            ToStatus = workOrder.Status,
            Kind = "Dispatch",
            Visibility = "Public",
            Note = "Vendor dispatch sent by SMS.",
            ChangedByUserId = command.ChangedByUserId,
            ChangedByLabel = command.ChangedByUserId.HasValue ? "Staff" : "System",
            CreatedAtUtc = command.DispatchedAtUtc,
        };
        _db.Add(statusEvent);
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
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
        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(WorkOrderStatusEvent),
            statusEvent.Id,
            AuditLogOperation.Created,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "System",
            NewValues: JsonSerializer.Serialize(new
            {
                workOrder.Id,
                FromStatus = workOrder.Status.ToString(),
                ToStatus = workOrder.Status.ToString(),
                statusEvent.Kind,
            }),
            ChangeReason: $"Work order #{workOrder.Id} vendor dispatch was appended to the status history."));
        context.StageOutbox(new OutboxMessage
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
        DispatchWorkOrderToVendorCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        if (command.ManagementAccess is null)
        {
            return;
        }

        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var authorized = await WhereManagementAuthorized(
                _db.Set<WorkOrder>().Where(workOrder =>
                    workOrder.Id == command.WorkOrderId &&
                    workOrder.PortfolioId == command.PortfolioId),
                _db,
                command.PortfolioId,
                command.ManagementAccess,
                now)
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The current workspace access no longer authorizes this work-order dispatch.");
        }
    }

    internal static IQueryable<WorkOrder> WhereManagementAuthorized(
        IQueryable<WorkOrder> workOrders,
        RentalCommandDbContext db,
        int portfolioId,
        DispatchManagementAccess? managementAccess,
        DateTime utcNow)
    {
        var access = managementAccess
            ?? throw new InvalidOperationException("Management access is required for this query.");
        return workOrders.Where(workOrder =>
            db.Set<AuthSession>().Any(session =>
                session.Id == access.SessionId &&
                session.UserId == access.UserId &&
                session.ActiveAccessContextId == access.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.Id == access.AccessContextId &&
                session.ActiveAccessContext.UserId == access.UserId &&
                session.ActiveAccessContext.PortfolioId == portfolioId &&
                session.ActiveAccessContext.AccessRevision == access.AccessRevision &&
                session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.SuspendedAtUtc == null &&
                session.ActiveAccessContext.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.PortfolioId == portfolioId &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == portfolioId &&
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
                          selected.PortfolioId == portfolioId &&
                          selected.PropertyId == workOrder.PropertyId))))));
    }

    private static DispatchWorkOrderToVendorResult Empty(
        DispatchWorkOrderToVendorOutcome outcome,
        DispatchWorkOrderToVendorCommand command) =>
        new(outcome, 0, command.PortfolioId, command.WorkOrderId, command.VendorId,
            VendorDispatchStatus.Dispatched, command.DispatchedAtUtc, null);
}
