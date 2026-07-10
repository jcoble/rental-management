using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Operations;

public sealed class DispatchWorkOrderToVendorHandler
    : IAtomicCommandHandler<DispatchWorkOrderToVendorCommand, DispatchWorkOrderToVendorResult>
{
    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public async Task<DispatchWorkOrderToVendorResult> HandleAsync(
        DispatchWorkOrderToVendorCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkOrder, command.WorkOrderId, ct);
        var workOrder = await attempt.Persistence.Query<WorkOrder>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.WorkOrderId
                && candidate.PortfolioId == command.PortfolioId, ct);
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

    private static DispatchWorkOrderToVendorResult Empty(
        DispatchWorkOrderToVendorOutcome outcome,
        DispatchWorkOrderToVendorCommand command) =>
        new(outcome, 0, command.PortfolioId, command.WorkOrderId, command.VendorId,
            VendorDispatchStatus.Dispatched, command.DispatchedAtUtc, null);
}
