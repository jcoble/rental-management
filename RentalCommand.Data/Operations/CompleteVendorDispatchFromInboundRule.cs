using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Operations;

public sealed class CompleteVendorDispatchFromInboundRule
{
    public const string ResultContract = "complete-vendor-dispatch-from-inbound-result.v1";

    private readonly RentalCommandDbContext _db;

    public CompleteVendorDispatchFromInboundRule(RentalCommandDbContext db) => _db = db;

    private static readonly VendorDispatchStatus[] OpenStatuses =
        [VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged];

    public static TransactionalWrite<CompleteVendorDispatchFromInboundCommand, CompleteVendorDispatchFromInboundResult> Write(
        CompleteVendorDispatchFromInboundCommand command,
        RentalCommandDbContext db)
    {
        var handler = new CompleteVendorDispatchFromInboundRule(db);
        var locks = command.IsCompletionRequest && !string.IsNullOrWhiteSpace(command.NormalizedFromPhone)
            ? new WriteLockPlan(WriteLockProtocol.VendorDispatchInbound,
                PhoneLockKey(command.NormalizedFromPhone))
            : WriteLockPlan.None;
        return new TransactionalWrite<CompleteVendorDispatchFromInboundCommand, CompleteVendorDispatchFromInboundResult>(
            "sms.vendor-done",  command, ResultContract, locks,
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<CompleteVendorDispatchFromInboundResult> ExecuteAsync(
        CompleteVendorDispatchFromInboundCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderEventId);
        if (!command.IsCompletionRequest || string.IsNullOrWhiteSpace(command.NormalizedFromPhone))
        {
            // The atomic receipt is still committed for every verified provider event. Non-DONE and
            // unmatchable senders are durable no-ops and never enter phone matching or mutation.
            return new CompleteVendorDispatchFromInboundResult(
                CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch, 0, 0, 0, 0, []);
        }

        // Different provider event ids from the same number must serialize before deciding which
        // open dispatch is eligible. A hash collision only over-serializes unrelated numbers.
        // The complete match, portfolio integrity checks, ordering, and limiting stay in one SQL
        // query. No free-form phone rows are materialized for in-memory normalization.
        var matches = await _db.Set<VendorDispatch>()
            .AsNoTracking()
            .TagWith("InboundVendorPhoneMatch: bounded top-two")
            .Where(candidate => OpenStatuses.Contains(candidate.Status)
                && candidate.Vendor != null
                && candidate.Vendor.PortfolioId == candidate.PortfolioId
                && candidate.Vendor.NormalizedPhone == command.NormalizedFromPhone
                && candidate.WorkOrder != null
                && candidate.WorkOrder.PortfolioId == candidate.PortfolioId
                && candidate.WorkOrder.Status != WorkOrderStatus.Cancelled
                && candidate.WorkOrder.Status != WorkOrderStatus.Archived
                && candidate.WorkOrder.DeletedAt == null)
            .OrderByDescending(candidate => candidate.DispatchedAtUtc)
            .ThenByDescending(candidate => candidate.Id)
            .Select(candidate => new { DispatchId = candidate.Id, candidate.WorkOrderId })
            .Take(2)
            .ToListAsync(ct);

        // Phone identity is not portfolio-qualified. Exactly one eligible open dispatch across the
        // entire system is required; two rows is enough to prove ambiguity without materializing all.
        if (matches.Count != 1)
        {
            return new CompleteVendorDispatchFromInboundResult(
                CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch, 0, 0, 0, 0, []);
        }

        // Phone locks serialize competing provider events for one sender; the work-order lock also
        // serializes sibling dispatches from different vendor phones. Load tracked state only after
        // that aggregate lock so the first real completion transition is decided from fresh rows.
        var match = matches[0];
        await context.AcquireLockAsync("WorkOrder", match.WorkOrderId, ct);
        var dispatch = await _db.Set<VendorDispatch>()
            .Include(candidate => candidate.Vendor)
            .Include(candidate => candidate.WorkOrder)
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == match.DispatchId
                && candidate.WorkOrderId == match.WorkOrderId
                && OpenStatuses.Contains(candidate.Status)
                && candidate.Vendor != null
                && candidate.Vendor.PortfolioId == candidate.PortfolioId
                && candidate.WorkOrder != null
                && candidate.WorkOrder.PortfolioId == candidate.PortfolioId
                && candidate.WorkOrder.Status != WorkOrderStatus.Cancelled
                && candidate.WorkOrder.Status != WorkOrderStatus.Archived
                && candidate.WorkOrder.DeletedAt == null,
                ct);
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
        var completedWorkOrder = previousStatus != WorkOrderStatus.Completed;

        dispatch.Status = VendorDispatchStatus.Completed;
        dispatch.RespondedAtUtc = receivedAt;

        WorkOrderStatusEvent? statusEvent = null;
        if (completedWorkOrder)
        {
            workOrder.Status = WorkOrderStatus.Completed;
            workOrder.CompletedAt = receivedAt;
            workOrder.UpdatedAt = receivedAt;
            context.BindSemanticAudit(workOrder, new AtomicSemanticAudit(
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
            _db.Add(statusEvent);
        }

        if (completedWorkOrder)
        {
            // A sibling dispatch can still be closed after another vendor completed the work order,
            // but scorecard credit belongs only to the first actual WorkOrder -> Completed transition.
            vendor.JobsCompleted += 1;
            vendor.UpdatedAt = receivedAt;
            context.BindSemanticAudit(vendor, new AtomicSemanticAudit(
                portfolioId,
                nameof(Vendor),
                vendor.Id,
                AuditLogOperation.Updated,
                NewValues: JsonSerializer.Serialize(new { JobsCompleted = vendor.JobsCompleted }),
                ChangeReason: "Vendor completed-jobs total advanced with the first work-order completion."));
        }

        List<Notification> notifications = completedWorkOrder
            ? await CreateNotificationsAsync(
                context, portfolioId, dispatch, workOrder, vendor.Name, receivedAt, ct)
            : [];
        if (notifications.Count > 0)
        {
            _db.AddRange(notifications);
        }

        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(new AtomicSemanticAudit(
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
            ChangeReason: completedWorkOrder
                ? $"Verified inbound provider event {command.ProviderEventId} completed work order #{workOrder.Id}."
                : $"Verified inbound provider event {command.ProviderEventId} closed a sibling dispatch for already-completed work order #{workOrder.Id}."));
        if (statusEvent is not null)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
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
            context.StageSemanticEvent(new AtomicSemanticAudit(
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

    public Task AuthorizeAsync(
        CompleteVendorDispatchFromInboundCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderEventId);
        if (command.ProviderEventId.Length > 200)
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Provider event id exceeds the inbound SMS provider metadata limit.");

        // SmsWebhookController verifies the SMS provider signature before every request reaches
        // this command. Replays must validate only the immutable delivery shape and preserve the
        // exact stored receipt, including NoOpenDispatch, even if phone/dispatch rows later change.
        return Task.CompletedTask;
    }

    private async Task<List<Notification>> CreateNotificationsAsync(
        IAtomicCommandContext commandContext,
        int portfolioId,
        VendorDispatch dispatch,
        WorkOrder workOrder,
        string vendorName,
        DateTime now,
        CancellationToken ct)
    {
        // The final recipients come from the saved work-order topic rule plus the current direct
        // work responsibility. Both branches are revalidated against one effective assignment's
        // work.read capability and property scope; only an explicitly configured admin fallback
        // applies when neither branch resolves anybody.
        var staffUserIds = ScopedNotificationRecipientQuery
            .ForTeamTopic(
                _db,
                portfolioId,
                TeamRoutingTopic.WorkOrders,
                workOrder.PropertyId,
                workOrder.Id,
                now);
        var recipients = await (
                from context in _db.Set<WorkspaceAccessContext>()
                join membership in _db.Set<WorkspaceMembership>()
                    on new { AccessContextId = context.Id, context.PortfolioId }
                    equals new { membership.AccessContextId, membership.PortfolioId }
                where context.PortfolioId == portfolioId
                    && staffUserIds.Contains(context.UserId)
                select new
                {
                    context.UserId,
                    AccessContextId = context.Id,
                    context.AccessRevision,
                    Experience = context.LastAuthorizedExperience ?? membership.DefaultExperience,
                })
            .Distinct()
            .OrderBy(recipient => recipient.UserId)
            .ToListAsync(ct);

        return recipients.Select(recipient =>
        {
            var notification = new Notification
            {
                PortfolioId = portfolioId,
                UserId = recipient.UserId,
                Type = "VendorJobCompleted",
                Title = "Job completed by vendor",
                Message = $"{vendorName} marked \"{workOrder.Title}\" complete by SMS.",
                Severity = "Success",
                RelatedEntityType = nameof(WorkOrder),
                RelatedEntityId = workOrder.Id,
                CreatedAt = now,
            };
            if (recipient.Experience is WorkspaceExperience.Management or WorkspaceExperience.Maintenance)
            {
                notification.NavigationExperience =
                    (NavigationExperience)(int)recipient.Experience;
                notification.NavigationDestination =
                    recipient.Experience == WorkspaceExperience.Maintenance
                        ? NavigationDestination.TechnicianWork
                        : NavigationDestination.WorkOrder;
                notification.NavigationAccessContextId = recipient.AccessContextId;
                notification.NavigationAccessRevision = recipient.AccessRevision;
                notification.NavigationResourceKind = nameof(WorkOrder);
                notification.NavigationResourceId = workOrder.Id;
                notification.NavigationAction = NavigationAction.Open;
                notification.NavigationExpiresAtUtc = now.AddDays(7);
                notification.NavigationFallbackDestination = NavigationDestination.Home;
            }
            return notification;
        }).ToList();
    }

    private static int PhoneLockKey(string normalizedPhone)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPhone));
        var key = BinaryPrimitives.ReadInt32BigEndian(digest) & int.MaxValue;
        return key == 0 ? 1 : key;
    }
}
