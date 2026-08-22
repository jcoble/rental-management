using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Operations;

public sealed class CancelVendorDispatchHandler
{
    public const string ResultContract = "vendor-dispatch.cancel.v1";

    private readonly RentalCommandDbContext _db;

    public CancelVendorDispatchHandler(RentalCommandDbContext db) => _db = db;

    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public static TransactionalWrite<CancelVendorDispatchCommand, CancelVendorDispatchResult> Write(
        CancelVendorDispatchCommand command,
        RentalCommandDbContext db)
    {
        var handler = new CancelVendorDispatchHandler(db);
        return new TransactionalWrite<CancelVendorDispatchCommand, CancelVendorDispatchResult>(
            "vendor-dispatch.cancel", WriteIdempotencyPolicy.Required, command, ResultContract,
            new WriteLockPlan(WriteLockProtocol.WorkOrder,
                command.WorkOrderId),
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<CancelVendorDispatchResult> ExecuteAsync(
        CancelVendorDispatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DeliveryIdempotencyKey);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? "Vendor dispatch cancelled."
            : command.Reason.Trim();
        if (reason.Length > 500)
        {
            throw new ArgumentException("Cancellation reason must be at most 500 characters.");
        }

        var workOrders = _db.Set<WorkOrder>()
            .Where(candidate => candidate.Id == command.WorkOrderId
                && candidate.PortfolioId == command.PortfolioId);
        if (command.ManagementAccess is not null)
        {
            var now = await context.ReadDatabaseClockUtcAsync(ct);
            workOrders = DispatchWorkOrderToVendorHandler.WhereManagementAuthorized(
                workOrders,
                _db,
                command.PortfolioId,
                command.ManagementAccess,
                now);
        }

        var workOrder = await workOrders.SingleOrDefaultAsync(ct);
        if (workOrder is null ||
            workOrder.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
        {
            return Empty(CancelVendorDispatchOutcome.NotFound, command);
        }

        var dispatch = await _db.Set<VendorDispatch>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.DispatchId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.WorkOrderId == command.WorkOrderId, ct);
        if (dispatch is null)
        {
            return Empty(CancelVendorDispatchOutcome.NotFound, command);
        }
        if (!OpenStatuses.Contains(dispatch.Status))
        {
            return new CancelVendorDispatchResult(
                CancelVendorDispatchOutcome.AlreadyClosed,
                dispatch.Id,
                dispatch.PortfolioId,
                dispatch.WorkOrderId,
                dispatch.VendorId,
                dispatch.Status,
                command.CancelledAtUtc,
                reason);
        }

        dispatch.Status = VendorDispatchStatus.Cancelled;
        if (workOrder.VendorId == dispatch.VendorId)
        {
            workOrder.VendorId = null;
        }
        workOrder.UpdatedAt = command.CancelledAtUtc;

        context.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(WorkOrder),
            workOrder.Id,
            AuditLogOperation.Updated,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
            NewValues: JsonSerializer.Serialize(new { workOrder.VendorId }),
            ChangeReason: $"Work order #{workOrder.Id} vendor dispatch #{dispatch.Id} was cancelled."));

        var statusEvent = new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = workOrder.Id,
            FromStatus = workOrder.Status,
            ToStatus = workOrder.Status,
            Kind = "Dispatch",
            Visibility = "Public",
            Note = reason,
            ChangedByUserId = command.ChangedByUserId,
            ChangedByLabel = command.ChangedByUserId.HasValue ? "Staff" : "System",
            CreatedAtUtc = command.CancelledAtUtc,
        };
        _db.Add(statusEvent);
        await context.FlushBusinessAsync(ct);

        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(VendorDispatch),
            dispatch.Id,
            AuditLogOperation.Updated,
            UserId: command.ChangedByUserId,
            ActorLabel: command.ChangedByUserId.HasValue ? null : "staff",
            NewValues: JsonSerializer.Serialize(new
            {
                dispatch.WorkOrderId,
                dispatch.VendorId,
                Status = dispatch.Status.ToString(),
            }),
            ChangeReason: $"Work order #{workOrder.Id} vendor dispatch #{dispatch.Id} was cancelled."));
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
                statusEvent.Note,
            }),
            ChangeReason: $"Work order #{workOrder.Id} vendor dispatch cancellation was appended to the status history."));
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = workOrder.Id }),
            IdempotencyKey = $"vendor-dispatch-cancel:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = command.CancelledAtUtc,
            NextAttemptAtUtc = command.CancelledAtUtc,
        });

        return new CancelVendorDispatchResult(
            CancelVendorDispatchOutcome.Cancelled,
            dispatch.Id,
            dispatch.PortfolioId,
            dispatch.WorkOrderId,
            dispatch.VendorId,
            dispatch.Status,
            command.CancelledAtUtc,
            reason);
    }

    public Task AuthorizeReplayAsync(
        CancelVendorDispatchCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RetiredPath();

    public async Task AuthorizeAsync(
        CancelVendorDispatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.ManagementAccess is null)
        {
            return;
        }

        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var authorized = await DispatchWorkOrderToVendorHandler.WhereManagementAuthorized(
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
                "The current workspace access no longer authorizes this vendor-dispatch cancellation.");
        }
    }

    private static InvalidOperationException RetiredPath() => new(
        "Vendor dispatch cancellations must use the shared write executor.");

    private static CancelVendorDispatchResult Empty(
        CancelVendorDispatchOutcome outcome,
        CancelVendorDispatchCommand command) =>
        new(outcome, command.DispatchId, command.PortfolioId, command.WorkOrderId, 0,
            VendorDispatchStatus.Cancelled, command.CancelledAtUtc, null);
}
