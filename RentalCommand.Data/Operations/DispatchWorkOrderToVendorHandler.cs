using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Operations;

public sealed class DispatchWorkOrderToVendorHandler
    : IAtomicCommandHandler<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult>
{
    public const string ResultContract = "vendor-dispatch.create.v1";

    private readonly RentalCommandDbContext _db;

    public DispatchWorkOrderToVendorHandler(RentalCommandDbContext db) => _db = db;

    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public static TransactionalWrite<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult> Write(
        DispatchWorkOrderToVendorCommand command,
        RentalCommandDbContext db)
    {
        var handler = new DispatchWorkOrderToVendorHandler(db);
        return new TransactionalWrite<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult>(
            "vendor-dispatch.create", WriteIdempotencyPolicy.Required, command, ResultContract,
            new WriteLockPlan(WriteLockProtocol.WorkOrder,
                WriteLock.For("WorkOrder", command.WorkOrderId)),
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public Task<DispatchWorkOrderToVendorResult> HandleAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public async Task<DispatchWorkOrderToVendorResult> ExecuteAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
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
        if (workOrder is null ||
            workOrder.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived ||
            !vendorExists)
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

    public Task AuthorizeReplayAsync(
        DispatchWorkOrderToVendorCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RetiredPath();

    public async Task AuthorizeAsync(
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

    private static InvalidOperationException RetiredPath() => new(
        "Vendor dispatches must use the shared write executor.");

    internal static IQueryable<WorkOrder> WhereManagementAuthorized(
        IQueryable<WorkOrder> workOrders,
        RentalCommandDbContext db,
        int portfolioId,
        DispatchManagementAccess? managementAccess,
        DateTime utcNow)
    {
        var access = managementAccess
            ?? throw new InvalidOperationException("Management access is required for this query.");
        var assignments = db.AuthorizedAssignmentsForScope(
            new WorkspaceReadScope(
                portfolioId,
                access.UserId,
                access.SessionId,
                access.AccessContextId,
                access.AccessRevision),
            [CapabilityKeys.WorkManage],
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        return workOrders.Where(workOrder =>
            assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 assignment.SelectedProperties.Any(selected =>
                     selected.PortfolioId == portfolioId &&
                     selected.PropertyId == workOrder.PropertyId))));
    }

    private static DispatchWorkOrderToVendorResult Empty(
        DispatchWorkOrderToVendorOutcome outcome,
        DispatchWorkOrderToVendorCommand command) =>
        new(outcome, 0, command.PortfolioId, command.WorkOrderId, command.VendorId,
            VendorDispatchStatus.Dispatched, command.DispatchedAtUtc, null);
}
