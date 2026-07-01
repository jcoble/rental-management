using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISmsInboundVendorDoneService"/>
public sealed class SmsInboundVendorDoneService : ISmsInboundVendorDoneService
{
    private const string WorkOrderEntityType = "WorkOrder";
    private const string DispatchEntityType = "VendorDispatch";

    private static readonly VendorDispatchStatus[] OpenStatuses =
        { VendorDispatchStatus.Dispatched, VendorDispatchStatus.Acknowledged };

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<SmsInboundVendorDoneService> _logger;
    private readonly TimeProvider _timeProvider;

    public SmsInboundVendorDoneService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        ILogger<SmsInboundVendorDoneService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<bool> CanHandleAsync(string? fromPhone, string? body, CancellationToken ct = default)
    {
        if (!IsDone(body))
        {
            return false;
        }

        var openDispatch = await FindOpenDispatchAsync(fromPhone, ct);
        return openDispatch is not null;
    }

    public async Task<SmsInboundVendorDoneResult> HandleAsync(
        string? fromPhone,
        string? body,
        DateTime receivedAtUtc,
        CancellationToken ct = default)
    {
        if (!IsDone(body))
        {
            return new SmsInboundVendorDoneResult(
                false, null, "Reply DONE when the job is complete.");
        }

        var dispatch = await FindOpenDispatchAsync(fromPhone, ct);
        if (dispatch is null)
        {
            return new SmsInboundVendorDoneResult(
                false, null, "We could not match this number to an open job.");
        }

        var workOrder = await _db.WorkOrders
            .FirstOrDefaultAsync(w => w.Id == dispatch.WorkOrderId && w.PortfolioId == dispatch.PortfolioId, ct);

        var portfolioId = dispatch.PortfolioId;
        var respondedAt = DateTime.SpecifyKind(receivedAtUtc, DateTimeKind.Utc);
        var nowUtc = _timeProvider.UtcNow();
        var normalizedFrom = SmsPhone.Normalize(fromPhone);

        dispatch.Status = VendorDispatchStatus.Completed;
        dispatch.RespondedAtUtc = respondedAt;

        WorkOrderStatus? previousStatus = null;
        if (workOrder is not null && workOrder.Status != WorkOrderStatus.Completed)
        {
            previousStatus = workOrder.Status;
            workOrder.Status = WorkOrderStatus.Completed;
            workOrder.CompletedAt = respondedAt;
            workOrder.UpdatedAt = nowUtc;

            workOrder.StatusEvents.Add(new WorkOrderStatusEvent
            {
                PortfolioId = portfolioId,
                FromStatus = previousStatus,
                ToStatus = WorkOrderStatus.Completed,
                Note = "Vendor replied DONE by SMS.",
                ChangedByUserId = null,
                ChangedByLabel = "Vendor",
                CreatedAtUtc = nowUtc,
            });
        }

        // Bump the vendor's completed-jobs counter once per closing DONE.
        var vendor = await _db.Vendors.FirstOrDefaultAsync(v => v.Id == dispatch.VendorId && v.PortfolioId == portfolioId, ct);
        if (vendor is not null)
        {
            vendor.JobsCompleted += 1;
            vendor.UpdatedAt = nowUtc;
        }

        await _db.SaveChangesAsync(ct);

        await SafeAsync("dispatch audit", () => _audit.LogAsync(
            portfolioId,
            DispatchEntityType,
            dispatch.Id,
            AuditLogOperation.Updated,
            actorLabel: "sms-inbound",
            newValues: JsonSerializer.Serialize(new
            {
                status = dispatch.Status.ToString(),
                respondedAtUtc = dispatch.RespondedAtUtc,
                workOrderId = dispatch.WorkOrderId,
            }),
            changeReason: $"Vendor replied DONE from {normalizedFrom} at {respondedAt:O}; work order #{dispatch.WorkOrderId} closed.",
            ct: ct));

        await NotifyLandlordAsync(portfolioId, dispatch, workOrder, vendor?.Name ?? "vendor", nowUtc, ct);

        return new SmsInboundVendorDoneResult(
            true,
            dispatch.WorkOrderId,
            "Thanks. We marked the job complete.");
    }

    /// <summary>
    /// Finds the most recent OPEN dispatch whose vendor's phone matches the inbound caller-id. Phone
    /// matching is normalized in memory (stored numbers are free-form) and scoped to the matched
    /// vendor — never across portfolios beyond the single dispatch we close.
    /// </summary>
    private async Task<VendorDispatch?> FindOpenDispatchAsync(string? fromPhone, CancellationToken ct)
    {
        var normalizedFrom = SmsPhone.Normalize(fromPhone);
        if (string.IsNullOrWhiteSpace(normalizedFrom))
        {
            return null;
        }

        var vendorIds = (await _db.Vendors
                .AsNoTracking()
                .Where(v => v.Phone != null)
                .Select(v => new { v.Id, v.Phone })
                .ToListAsync(ct))
            .Where(v => SmsPhone.Normalize(v.Phone) == normalizedFrom)
            .Select(v => v.Id)
            .ToHashSet();

        if (vendorIds.Count == 0)
        {
            return null;
        }

        return await _db.VendorDispatches
            .Where(d => vendorIds.Contains(d.VendorId) && OpenStatuses.Contains(d.Status))
            .OrderByDescending(d => d.DispatchedAtUtc)
            .ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task NotifyLandlordAsync(
        int portfolioId,
        VendorDispatch dispatch,
        WorkOrder? workOrder,
        string vendorName,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var title = "Job completed by vendor";
        var jobTitle = workOrder?.Title ?? $"work order #{dispatch.WorkOrderId}";
        var message = $"{vendorName} marked \"{jobTitle}\" complete by SMS.";

        await SafeAsync("in-app notification", async () =>
        {
            var staffUserIds = await StaffUserIdsAsync(portfolioId, ct);
            if (staffUserIds.Count == 0) return;

            var notifications = staffUserIds.Select(userId => new Notification
            {
                PortfolioId = portfolioId,
                UserId = userId,
                Type = "VendorJobCompleted",
                Title = title,
                Message = message,
                Severity = "Success",
                ActionUrl = $"/work-orders?workOrderId={dispatch.WorkOrderId}",
                RelatedEntityType = WorkOrderEntityType,
                RelatedEntityId = dispatch.WorkOrderId,
                CreatedAt = nowUtc,
            }).ToList();

            _db.Notifications.AddRange(notifications);
            await _db.SaveChangesAsync(ct);

            foreach (var notification in notifications)
            {
                await SafeAsync("notification broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
                    portfolioId, "Notification", notification.Id, NotificationResponse.FromEntity(notification), ct));
            }
        });

        if (workOrder is not null)
        {
            await SafeAsync("work order broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, WorkOrderEntityType, workOrder.Id, WorkOrderResponse.FromEntity(workOrder), ct));
        }

        await SafeAsync("dispatch broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, DispatchEntityType, dispatch.Id, VendorDispatchResponse.FromEntity(dispatch), ct));
    }

    private async Task<IReadOnlyList<int>> StaffUserIdsAsync(int portfolioId, CancellationToken ct)
    {
        var staffRoles = new[] { nameof(UserRole.Admin), nameof(UserRole.Manager), nameof(UserRole.Agent), nameof(UserRole.Owner) };

        return await (
                from user in _db.Users.AsNoTracking()
                join userRole in _db.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where user.PortfolioId == portfolioId && role.Name != null && staffRoles.Contains(role.Name)
                select user.Id)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>Deterministic DONE detection — literal completion keywords only, no LLM.</summary>
    private static bool IsDone(string? body)
    {
        var normalized = body?.Trim().Trim('.', '!', '?', ',').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var keywords = new[]
        {
            "DONE", "COMPLETE", "COMPLETED", "FINISHED", "FINISH", "JOB DONE", "ALL DONE", "DONE NOW",
        };
        if (keywords.Contains(normalized))
        {
            return true;
        }

        return normalized.StartsWith("DONE ", StringComparison.Ordinal) ||
               normalized.StartsWith("COMPLETE ", StringComparison.Ordinal) ||
               normalized.StartsWith("COMPLETED ", StringComparison.Ordinal) ||
               normalized.StartsWith("FINISHED ", StringComparison.Ordinal);
    }

    private async Task SafeAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMS vendor-done side effect '{Label}' failed (continuing).", label);
        }
    }
}
