using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IWorkOrderService"/>
public class WorkOrderService : IWorkOrderService
{
    private const string EntityType = "WorkOrder";

    // Work-order statuses a user can actually move a ticket INTO. These are exactly the values the web
    // and mobile detail screens expose as transition targets (web's STATUS_TRANSITIONS map and mobile's
    // _allStatuses list). Moves AMONG these six are left fully open on purpose: the mobile UI offers
    // every other core status as a target from any non-current status — including reopening a Completed
    // or Cancelled ticket (web also exposes Cancelled→New) — so a stricter per-edge graph would reject
    // a path the shipping apps legitimately drive. What this set DOES reject via the generic PATCH is a
    // jump to a status no flow ever assigns (OnHold / Archived) — those are read-only/reporting states,
    // never user-set — and any undefined enum value the wire smuggles past JsonStringEnumConverter as a
    // raw integer. A same→same PATCH is always allowed (so re-sending a current OnHold/Archived, or a
    // PATCH that only touches costs/dates, never trips this).
    private static readonly IReadOnlySet<WorkOrderStatus> UserAssignableStatuses = new HashSet<WorkOrderStatus>
    {
        WorkOrderStatus.New,
        WorkOrderStatus.Scheduled,
        WorkOrderStatus.InProgress,
        WorkOrderStatus.WaitingParts,
        WorkOrderStatus.Completed,
        WorkOrderStatus.Cancelled,
    };

    // A CompletedAt further than this past "now" is treated as a typo/garbage entry rather than a real
    // completion time. Generous enough to never reject a legitimately back- or forward-dated entry.
    private static readonly TimeSpan MaxCompletedAtFutureSkew = TimeSpan.FromDays(1);

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<WorkOrderService> _logger;

    public WorkOrderService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IMessagePublisher publisher,
        ILogger<WorkOrderService> logger)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> ListAsync(int portfolioId, int? propertyId, int? vendorId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(w => w.PropertyId == propertyId.Value);
        }

        if (vendorId.HasValue)
        {
            q = q.Where(w => w.VendorId == vendorId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(w =>
                EF.Functions.ILike(w.Title, $"%{term}%") ||
                EF.Functions.ILike(w.Description, $"%{term}%") ||
                EF.Functions.ILike(w.Category, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "title" => query.SortDescending ? q.OrderByDescending(w => w.Title) : q.OrderBy(w => w.Title),
            "status" => query.SortDescending ? q.OrderByDescending(w => w.Status) : q.OrderBy(w => w.Status),
            "priority" => query.SortDescending ? q.OrderByDescending(w => w.Priority) : q.OrderBy(w => w.Priority),
            "requestedat" => query.SortDescending ? q.OrderByDescending(w => w.RequestedAt) : q.OrderBy(w => w.RequestedAt),
            "scheduledfor" => query.SortDescending ? q.OrderByDescending(w => w.ScheduledFor) : q.OrderBy(w => w.ScheduledFor),
            "updatedat" => query.SortDescending ? q.OrderByDescending(w => w.UpdatedAt) : q.OrderBy(w => w.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(w => w.RequestedAt) : q.OrderBy(w => w.RequestedAt),
        };

        // Project the display names (property / unit / vendor / tenant) in the same SELECT via LEFT
        // JOINs — EF translates the optional-navigation member access to a join, so there is no
        // per-row follow-up query. The full related entities are never materialized; only the name
        // columns ride along.
        var rows = await q
            .Select(w => new WorkOrderListRow(
                w,
                w.Property != null ? w.Property.Name : null,
                w.Unit != null ? w.Unit.UnitNumber : null,
                w.Vendor != null ? w.Vendor.Name : null,
                w.Tenant != null ? ((w.Tenant.FirstName + " " + w.Tenant.LastName)).Trim() : null))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return rows.Select(ToListResponse).ToList();
    }

    private sealed record WorkOrderListRow(
        WorkOrder WorkOrder, string? PropertyName, string? UnitNumber, string? VendorName, string? TenantName);

    private static WorkOrderResponse ToListResponse(WorkOrderListRow row)
    {
        var response = WorkOrderResponse.FromEntity(row.WorkOrder);
        response.PropertyName = row.PropertyName;
        response.UnitNumber = row.UnitNumber;
        response.VendorName = row.VendorName;
        response.TenantName = row.TenantName;
        return response;
    }

    public async Task<WorkOrderDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.WorkOrders
            .AsNoTracking()
            .Include(w => w.Property)
            .Include(w => w.Unit)
            .Include(w => w.Vendor)
            .Include(w => w.Tenant)
            .FirstOrDefaultAsync(w => w.Id == id && w.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var events = await _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == id && e.PortfolioId == portfolioId)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var response = WorkOrderDetailResponse.FromEntity(entity, events);
        var scan = await _db.FindLatestEntityFileAsync(portfolioId, "WorkOrder", id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<WorkOrderResponse?> CreateAsync(int portfolioId, CreateWorkOrderRequest request, int? changedByUserId = null, string? changedByLabel = null, CancellationToken ct = default)
    {
        // Verify the referenced property (required) and optional unit/tenant/lease/vendor are in scope.
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.TenantId.HasValue &&
            !await _db.EnsureTenantInPortfolioAsync(portfolioId, request.TenantId.Value, ct))
        {
            return null;
        }

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new WorkOrder
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            TenantId = request.TenantId,
            LeaseId = request.LeaseId,
            VendorId = request.VendorId,
            Title = request.Title,
            Description = request.Description,
            Category = request.Category,
            Priority = request.Priority,
            Status = request.Status,
            RequestedAt = request.RequestedAt?.ToUtc() ?? now,
            ScheduledFor = request.ScheduledFor.ToUtcDateTime(),
            ScheduledWindowEnd = request.ScheduledWindowEnd.ToUtcDateTime(),
            CompletedAt = request.CompletedAt.ToUtc(),
            EstimatedCost = request.EstimatedCost,
            ActualCost = request.ActualCost,
            CreatedBy = request.CreatedBy,
            ExtractedData = request.ExtractedData,
            UpdatedAt = now,
        };

        _db.WorkOrders.Add(entity);

        // Initial timeline entry: null → the created status. Same save as the work order so the
        // stream can never diverge from the current status.
        entity.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = portfolioId,
            FromStatus = null,
            ToStatus = entity.Status,
            Note = null,
            ChangedByUserId = changedByUserId,
            ChangedByLabel = changedByLabel,
            CreatedAtUtc = now,
        });

        await _db.SaveChangesAsync(ct);

        // When the work order is scheduled with an arrival window AND tied to a tenant, text the tenant
        // the appointment window so they know when to expect access. The original offset-bearing window
        // (the landlord's local time) is passed through so the SMS renders in their local time rather
        // than UTC. Best-effort: a notification failure must never roll back the created work order.
        await NotifyTenantOfScheduleAsync(portfolioId, entity, request.ScheduledFor, request.ScheduledWindowEnd, ct);

        var response = WorkOrderResponse.FromEntity(entity);
        await HydrateDisplayNamesAsync(portfolioId, response, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    /// <summary>
    /// Enqueues a tenant-facing SMS describing the scheduled arrival window for a newly created work
    /// order. No-ops unless the work order has a tenant, a <see cref="WorkOrder.ScheduledFor"/>, and a
    /// <see cref="WorkOrder.ScheduledWindowEnd"/>, and that tenant has a phone number on file. The
    /// <paramref name="localStart"/>/<paramref name="localEnd"/> are the original offset-bearing values
    /// the client sent (the landlord's local time) so the SMS is rendered in that local time rather than
    /// UTC. Wrapped so any failure (missing phone, outbox error) is logged and swallowed rather than
    /// failing the create.
    /// </summary>
    private async Task NotifyTenantOfScheduleAsync(
        int portfolioId, WorkOrder entity, DateTimeOffset? localStart, DateTimeOffset? localEnd, CancellationToken ct)
    {
        if (entity.TenantId is null || entity.ScheduledFor is null || entity.ScheduledWindowEnd is null
            || localStart is null || localEnd is null)
        {
            return;
        }

        try
        {
            var tenant = await _db.Tenants
                .AsNoTracking()
                .Where(t => t.Id == entity.TenantId.Value && t.PortfolioId == portfolioId)
                .Select(t => new { t.Phone })
                .FirstOrDefaultAsync(ct);

            var tenantPhone = SmsPhone.Normalize(tenant?.Phone);
            if (string.IsNullOrWhiteSpace(tenantPhone))
            {
                return;
            }

            var message = BuildTenantScheduleSms(entity.Title, localStart.Value, localEnd.Value);
            await _publisher.PublishAsync(portfolioId, "sms", new
            {
                to = tenantPhone,
                message,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tenant schedule notification for work order #{WorkOrderId} failed (continuing).", entity.Id);
        }
    }

    /// <summary>
    /// Builds the tenant arrival-window SMS. The window is rendered in the landlord's local time — the
    /// caller passes the original offset-bearing <see cref="DateTimeOffset"/> values the client sent, so
    /// formatting their wall-clock component yields the local time the tenant should expect access. No
    /// timezone label is emitted (a residential tenant can't reconcile "UTC"); a per-tenant/per-property
    /// timezone preference can refine this later.
    /// </summary>
    private static string BuildTenantScheduleSms(string title, DateTimeOffset start, DateTimeOffset end)
    {
        var sb = new StringBuilder();
        sb.Append($"Maintenance scheduled for {title}: ");
        sb.Append(start.ToString("ddd MMM d, h:mm tt"));
        sb.Append(" – ");
        // Same-day window: show only the end time; otherwise show the full end date too.
        sb.Append(end.Date == start.Date
            ? end.ToString("h:mm tt")
            : end.ToString("ddd MMM d, h:mm tt"));
        sb.Append(". Please ensure access is available during this window.");
        return sb.ToString();
    }

    /// <summary>
    /// Fill the property / unit / vendor / tenant display names on a response after a write, in a
    /// single SQL projection keyed by id (LEFT JOINs to the related rows — no full entities loaded).
    /// Keeps the PATCH/POST result and the SignalR broadcast carrying the same labels the list/detail
    /// reads project, so the grid never flashes a blank property name on a live update.
    /// </summary>
    private async Task HydrateDisplayNamesAsync(int portfolioId, WorkOrderResponse response, CancellationToken ct)
    {
        var names = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.Id == response.Id && w.PortfolioId == portfolioId)
            .Select(w => new
            {
                PropertyName = w.Property != null ? w.Property.Name : null,
                UnitNumber = w.Unit != null ? w.Unit.UnitNumber : null,
                VendorName = w.Vendor != null ? w.Vendor.Name : null,
                TenantName = w.Tenant != null ? ((w.Tenant.FirstName + " " + w.Tenant.LastName)).Trim() : null,
            })
            .FirstOrDefaultAsync(ct);

        if (names is null)
        {
            return;
        }

        response.PropertyName = names.PropertyName;
        response.UnitNumber = names.UnitNumber;
        response.VendorName = names.VendorName;
        response.TenantName = names.TenantName;
    }

    public async Task<WorkOrderResponse?> UpdateAsync(int portfolioId, int id, UpdateWorkOrderRequest request, int? changedByUserId = null, string? changedByLabel = null, CancellationToken ct = default)
    {
        var entity = await _db.WorkOrders
            .FirstOrDefaultAsync(w => w.Id == id && w.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, entity.PropertyId, ct))
        {
            return null;
        }

        if (request.TenantId.HasValue &&
            !await _db.EnsureTenantInPortfolioAsync(portfolioId, request.TenantId.Value, ct))
        {
            return null;
        }

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
        {
            return null;
        }

        if (request.VendorId.HasValue &&
            !await _db.EnsureVendorInPortfolioAsync(portfolioId, request.VendorId.Value, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.TenantId.HasValue) entity.TenantId = request.TenantId;
        if (request.LeaseId.HasValue) entity.LeaseId = request.LeaseId;
        if (request.VendorId.HasValue) entity.VendorId = request.VendorId;
        if (request.Title != null) entity.Title = request.Title;
        if (request.Description != null) entity.Description = request.Description;
        if (request.Category != null) entity.Category = request.Category;
        if (request.Priority.HasValue) entity.Priority = request.Priority.Value;

        // Capture the status transition (if any) so we can append a timeline entry in the same save.
        var previousStatus = entity.Status;
        var statusChanged = request.Status.HasValue && request.Status.Value != previousStatus;
        if (statusChanged)
        {
            EnsureStatusAssignable(request.Status!.Value);
        }
        if (request.Status.HasValue) entity.Status = request.Status.Value;

        if (request.RequestedAt.HasValue) entity.RequestedAt = request.RequestedAt.Value.ToUtc();
        if (request.ScheduledFor.HasValue) entity.ScheduledFor = request.ScheduledFor.ToUtcDateTime();
        if (request.ScheduledWindowEnd.HasValue) entity.ScheduledWindowEnd = request.ScheduledWindowEnd.ToUtcDateTime();
        if (request.CompletedAt.HasValue) entity.CompletedAt = request.CompletedAt.ToUtc();
        if (request.EstimatedCost.HasValue) entity.EstimatedCost = request.EstimatedCost;
        if (request.ActualCost.HasValue) entity.ActualCost = request.ActualCost;
        var now = DateTime.UtcNow;

        // A supplied completion timestamp can't sit unreasonably far in the future, and an explicitly
        // inverted pair (the client sends BOTH RequestedAt and CompletedAt with completed < requested in
        // the same request) is rejected. We deliberately do NOT compare a lone CompletedAt PATCH against
        // the stored RequestedAt: RequestedAt auto-defaults to creation time, so back-dating only the
        // completion date on an existing/closed order (a supported edit — there is no reopen workflow) is
        // legitimate and must not be blocked. An out-of-order pair corrupts age/SLA reporting; the future
        // bound catches typo'd far-future dates.
        EnsureCompletedAtInRange(
            request.RequestedAt.HasValue, entity.RequestedAt,
            request.CompletedAt.HasValue, entity.CompletedAt,
            now);

        entity.UpdatedAt = now;

        if (statusChanged)
        {
            entity.StatusEvents.Add(new WorkOrderStatusEvent
            {
                PortfolioId = portfolioId,
                FromStatus = previousStatus,
                ToStatus = entity.Status,
                Note = string.IsNullOrWhiteSpace(request.StatusNote) ? null : request.StatusNote.Trim(),
                ChangedByUserId = changedByUserId,
                ChangedByLabel = changedByLabel,
                CreatedAtUtc = now,
            });
        }

        await _db.SaveChangesAsync(ct);

        var response = WorkOrderResponse.FromEntity(entity);
        await HydrateDisplayNamesAsync(portfolioId, response, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    // Reject a status PATCH that targets a status no UI ever assigns (OnHold / Archived are reporting-only
    // states) or an undefined enum value. Moves among the user-assignable statuses — including reopening a
    // closed ticket — are intentionally left open to match the web + mobile detail screens. A same→same
    // move never reaches here (the caller only validates an actual change). Throws a 400.
    private static void EnsureStatusAssignable(WorkOrderStatus to)
    {
        if (!UserAssignableStatuses.Contains(to))
        {
            throw new DomainValidationException(
                $"A work order cannot be moved to {to}.");
        }
    }

    // Validates the (RequestedAt, CompletedAt) pair on an update. A supplied CompletedAt must not be far
    // in the future. The "completed before requested" check only fires when the SAME request explicitly
    // sets BOTH dates — an inverted pair the user actually typed — so back-dating a lone CompletedAt
    // against an auto-defaulted RequestedAt (a supported edit on a completed order) is never blocked.
    // Throws a 400.
    private static void EnsureCompletedAtInRange(
        bool requestedProvided, DateTime effectiveRequestedAt,
        bool completedProvided, DateTime? effectiveCompletedAt,
        DateTime nowUtc)
    {
        if (effectiveCompletedAt is not { } completed)
        {
            return;
        }

        if (completedProvided && completed > nowUtc + MaxCompletedAtFutureSkew)
        {
            throw new DomainValidationException(
                "The completion date can't be in the future.");
        }

        if (requestedProvided && completedProvided && completed < effectiveRequestedAt)
        {
            throw new DomainValidationException(
                "The completion date can't be before the work order was requested.");
        }
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.WorkOrders
            .FirstOrDefaultAsync(w => w.Id == id && w.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // Soft-delete: preserve the maintenance record (consistent with the other entities +
        // keeps an audit trail). The global query filter hides it from all reads.
        entity.DeletedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
