using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class CompleteVendorDispatchFromInboundHandler
    : IAtomicCommandHandler<CompleteVendorDispatchFromInboundCommand, CompleteVendorDispatchFromInboundResult>
{
    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public async Task<CompleteVendorDispatchFromInboundResult> HandleAsync(
        CompleteVendorDispatchFromInboundCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.NormalizedFromPhone);

        // Different provider event ids from the same number must serialize before deciding which
        // open dispatch is eligible. A hash collision only over-serializes unrelated numbers.
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.VendorDispatch,
            PhoneLockKey(command.NormalizedFromPhone),
            ct);

        // The complete match, portfolio integrity checks, ordering, and limiting stay in one SQL
        // query. No free-form phone rows are materialized for in-memory normalization.
        var dispatch = await attempt.Persistence.Query<VendorDispatch>()
            .Include(candidate => candidate.Vendor)
            .Include(candidate => candidate.WorkOrder)
            .Where(candidate => OpenStatuses.Contains(candidate.Status)
                && candidate.Vendor != null
                && candidate.Vendor.PortfolioId == candidate.PortfolioId
                && candidate.Vendor.NormalizedPhone == command.NormalizedFromPhone
                && candidate.WorkOrder != null
                && candidate.WorkOrder.PortfolioId == candidate.PortfolioId
                && candidate.WorkOrder.DeletedAt == null
                && !attempt.Persistence.Query<VendorDispatch>().Any(other =>
                    other.Id != candidate.Id
                    && other.PortfolioId != candidate.PortfolioId
                    && OpenStatuses.Contains(other.Status)
                    && other.Vendor != null
                    && other.Vendor.PortfolioId == other.PortfolioId
                    && other.Vendor.NormalizedPhone == command.NormalizedFromPhone
                    && other.WorkOrder != null
                    && other.WorkOrder.PortfolioId == other.PortfolioId
                    && other.WorkOrder.DeletedAt == null))
            .OrderByDescending(candidate => candidate.DispatchedAtUtc)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(ct);

        if (dispatch?.Vendor is null || dispatch.WorkOrder is null)
        {
            return new CompleteVendorDispatchFromInboundResult(
                CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch, 0, 0, 0, 0, []);
        }

        var workOrder = dispatch.WorkOrder;
        var vendor = dispatch.Vendor;
        var portfolioId = dispatch.PortfolioId;
        var receivedAt = DateTime.SpecifyKind(command.ReceivedAtUtc, DateTimeKind.Utc);
        var previousStatus = workOrder.Status;

        dispatch.Status = VendorDispatchStatus.Completed;
        dispatch.RespondedAtUtc = receivedAt;

        WorkOrderStatusEvent? statusEvent = null;
        if (workOrder.Status != WorkOrderStatus.Completed)
        {
            workOrder.Status = WorkOrderStatus.Completed;
            workOrder.CompletedAt = receivedAt;
            workOrder.UpdatedAt = receivedAt;
            attempt.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
                portfolioId,
                nameof(WorkOrder),
                workOrder.Id,
                AuditLogOperation.Updated,
                NewValues: JsonSerializer.Serialize(new
                {
                    fromStatus = previousStatus.ToString(),
                    toStatus = WorkOrderStatus.Completed.ToString(),
                    completedAt = receivedAt,
                    command.ProviderEventId,
                }),
                ChangeReason: "Vendor replied DONE through a verified SMS provider event."));

            statusEvent = new WorkOrderStatusEvent
            {
                PortfolioId = portfolioId,
                WorkOrderId = workOrder.Id,
                FromStatus = previousStatus,
                ToStatus = WorkOrderStatus.Completed,
                Note = "Vendor replied DONE by SMS.",
                ChangedByUserId = null,
                ChangedByLabel = "Vendor",
                CreatedAtUtc = receivedAt,
            };
            attempt.Persistence.Add(statusEvent);
        }

        vendor.JobsCompleted += 1;
        vendor.UpdatedAt = receivedAt;
        attempt.BindSemanticAudit(vendor, new AtomicSemanticAudit(
            portfolioId,
            nameof(Vendor),
            vendor.Id,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new { JobsCompleted = vendor.JobsCompleted }),
            ChangeReason: "Vendor completed-jobs total advanced with the atomic dispatch completion."));

        var notifications = await CreateNotificationsAsync(
            attempt, portfolioId, dispatch, workOrder, vendor.Name, receivedAt, ct);
        if (notifications.Count > 0)
        {
            attempt.Persistence.AddRange(notifications);
        }

        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            portfolioId,
            nameof(VendorDispatch),
            dispatch.Id,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new
            {
                status = VendorDispatchStatus.Completed.ToString(),
                respondedAtUtc = receivedAt,
                command.ProviderEventId,
            }),
            ChangeReason: $"Verified inbound provider event {command.ProviderEventId} completed work order #{workOrder.Id}."));
        if (statusEvent is not null)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                portfolioId,
                nameof(WorkOrderStatusEvent),
                statusEvent.Id,
                AuditLogOperation.Created,
                NewValues: JsonSerializer.Serialize(new
                {
                    workOrder.Id,
                    FromStatus = previousStatus.ToString(),
                    ToStatus = WorkOrderStatus.Completed.ToString(),
                    command.ProviderEventId,
                }),
                ChangeReason: "Vendor completion appended to the work-order status history."));
        }
        foreach (var notification in notifications)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                portfolioId,
                nameof(Notification),
                notification.Id,
                AuditLogOperation.Created,
                NewValues: JsonSerializer.Serialize(new
                {
                    notification.UserId,
                    notification.Type,
                    notification.RelatedEntityId,
                    command.ProviderEventId,
                }),
                ChangeReason: "Staff notification committed with vendor completion."));
        }

        return new CompleteVendorDispatchFromInboundResult(
            CompleteVendorDispatchFromInboundOutcome.Applied,
            portfolioId,
            dispatch.Id,
            workOrder.Id,
            vendor.Id,
            notifications.Select(notification => notification.Id).ToArray());
    }

    private static async Task<List<Notification>> CreateNotificationsAsync(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        VendorDispatch dispatch,
        WorkOrder workOrder,
        string vendorName,
        DateTime now,
        CancellationToken ct)
    {
        var staffRoles = new[]
        {
            nameof(UserRole.Admin), nameof(UserRole.Manager), nameof(UserRole.Agent), nameof(UserRole.Owner),
        };
        var staffUserIds = await (
                from user in attempt.Persistence.Query<ApplicationUser>()
                join userRole in attempt.Persistence.Query<Microsoft.AspNetCore.Identity.IdentityUserRole<int>>()
                    on user.Id equals userRole.UserId
                join role in attempt.Persistence.Query<Microsoft.AspNetCore.Identity.IdentityRole<int>>()
                    on userRole.RoleId equals role.Id
                where user.PortfolioId == portfolioId
                    && role.Name != null
                    && staffRoles.Contains(role.Name)
                select user.Id)
            .Distinct()
            .OrderBy(userId => userId)
            .ToListAsync(ct);

        return staffUserIds.Select(userId => new Notification
        {
            PortfolioId = portfolioId,
            UserId = userId,
            Type = "VendorJobCompleted",
            Title = "Job completed by vendor",
            Message = $"{vendorName} marked \"{workOrder.Title}\" complete by SMS.",
            Severity = "Success",
            ActionUrl = $"/work-orders?workOrderId={workOrder.Id}",
            RelatedEntityType = nameof(WorkOrder),
            RelatedEntityId = workOrder.Id,
            CreatedAt = now,
        }).ToList();
    }

    private static int PhoneLockKey(string normalizedPhone)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPhone));
        var key = BinaryPrimitives.ReadInt32BigEndian(digest) & int.MaxValue;
        return key == 0 ? 1 : key;
    }
}
