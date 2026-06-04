using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IWorkOrderService"/>
public class WorkOrderService : IWorkOrderService
{
    private const string EntityType = "WorkOrder";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public WorkOrderService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
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

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(WorkOrderResponse.FromEntity).ToList();
    }

    public async Task<WorkOrderDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.WorkOrders
            .AsNoTracking()
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

        return WorkOrderDetailResponse.FromEntity(entity, events);
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
            ScheduledFor = request.ScheduledFor.ToUtc(),
            CompletedAt = request.CompletedAt.ToUtc(),
            EstimatedCost = request.EstimatedCost,
            ActualCost = request.ActualCost,
            CreatedBy = request.CreatedBy,
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

        var response = WorkOrderResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
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
        if (request.Status.HasValue) entity.Status = request.Status.Value;

        if (request.ScheduledFor.HasValue) entity.ScheduledFor = request.ScheduledFor.ToUtc();
        if (request.CompletedAt.HasValue) entity.CompletedAt = request.CompletedAt.ToUtc();
        if (request.EstimatedCost.HasValue) entity.EstimatedCost = request.EstimatedCost;
        if (request.ActualCost.HasValue) entity.ActualCost = request.ActualCost;
        var now = DateTime.UtcNow;
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
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
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
