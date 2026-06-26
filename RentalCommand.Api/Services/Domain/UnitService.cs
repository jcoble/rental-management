using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IUnitService"/>
public class UnitService : IUnitService
{
    private const string EntityType = "Unit";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;

    public UnitService(RentalCommandDbContext db, IDataUpdateService dataUpdate, IAuditTrailService audit)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
    }

    public async Task<IReadOnlyList<UnitResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        // Scope through the owning Property's portfolio; Unit has no PortfolioId of its own.
        var q = _db.Units
            .AsNoTracking()
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(u => u.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(u =>
                EF.Functions.ILike(u.UnitNumber, $"%{term}%") ||
                (u.FloorPlan != null && EF.Functions.ILike(u.FloorPlan, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "unitnumber" => query.SortDescending ? q.OrderByDescending(u => u.UnitNumber) : q.OrderBy(u => u.UnitNumber),
            "marketrent" => query.SortDescending ? q.OrderByDescending(u => u.MarketRent) : q.OrderBy(u => u.MarketRent),
            "status" => query.SortDescending ? q.OrderByDescending(u => u.Status) : q.OrderBy(u => u.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(u => u.UpdatedAt) : q.OrderBy(u => u.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(u => u.CreatedAt) : q.OrderBy(u => u.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(UnitResponse.FromEntity).ToList();
    }

    public async Task<IReadOnlyList<UnitHealthResponse>> ListWithHealthAsync(
        int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListWithHealthPageAsync(portfolioId, ToUnitHealthListQuery(query, propertyId), ct);
        return page.Items;
    }

    public async Task<UnitHealthListResponse> ListWithHealthPageAsync(
        int portfolioId, UnitHealthListQuery query, CancellationToken ct = default)
    {
        // Scope through the owning Property's portfolio; Unit has no PortfolioId of its own.
        var q = _db.Units
            .AsNoTracking()
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId);

        if (query.PropertyId.HasValue)
        {
            q = q.Where(u => u.PropertyId == query.PropertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(u =>
                EF.Functions.ILike(u.UnitNumber, $"%{term}%") ||
                (u.Property != null && EF.Functions.ILike(u.Property.Name, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "unitnumber" => query.SortDescending ? q.OrderByDescending(u => u.UnitNumber) : q.OrderBy(u => u.UnitNumber),
            "propertyname" => query.SortDescending ? q.OrderByDescending(u => u.Property!.Name) : q.OrderBy(u => u.Property!.Name),
            "openworkordercount" => query.SortDescending ? q.OrderByDescending(u => u.WorkOrders.Count(w =>
                w.Status != WorkOrderStatus.Completed &&
                w.Status != WorkOrderStatus.Cancelled &&
                w.Status != WorkOrderStatus.Archived)) : q.OrderBy(u => u.WorkOrders.Count(w =>
                w.Status != WorkOrderStatus.Completed &&
                w.Status != WorkOrderStatus.Cancelled &&
                w.Status != WorkOrderStatus.Archived)),
            "marketrent" => query.SortDescending ? q.OrderByDescending(u => u.MarketRent) : q.OrderBy(u => u.MarketRent),
            "status" => query.SortDescending ? q.OrderByDescending(u => u.Status) : q.OrderBy(u => u.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(u => u.UpdatedAt) : q.OrderBy(u => u.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(u => u.UnitNumber) : q.OrderBy(u => u.UnitNumber),
        };

        var totalCount = await q.CountAsync(ct);
        var now = DateTime.UtcNow;

        // One projection query: the health badges are correlated subqueries (grouped counts + the active
        // lease's scalars). No per-unit dashboard call, no N+1 — the only in-memory step is formatting the
        // simplified stage label from the already-projected scalars.
        var rows = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(u => new
            {
                u.Id,
                u.PropertyId,
                PropertyName = u.Property!.Name,
                u.UnitNumber,
                u.Status,
                u.MarketRent,
                OpenWorkOrderCount = u.WorkOrders.Count(w =>
                    w.Status != WorkOrderStatus.Completed &&
                    w.Status != WorkOrderStatus.Cancelled &&
                    w.Status != WorkOrderStatus.Archived),
                // Current lease signal for list badges: Active leases, plus NoticeGiven leases that are
                // still occupied but moving out. Kept as correlated SQL subqueries.
                CurrentLeaseStatus = u.Leases
                    .Where(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)
                    .OrderByDescending(l => l.Status == LeaseStatus.Active)
                    .ThenByDescending(l => l.StartDate)
                    .ThenByDescending(l => l.Id)
                    .Select(l => (LeaseStatus?)l.Status)
                    .FirstOrDefault(),
                CurrentLeaseEndDate = u.Leases
                    .Where(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven)
                    .OrderByDescending(l => l.Status == LeaseStatus.Active)
                    .ThenByDescending(l => l.StartDate)
                    .ThenByDescending(l => l.Id)
                    .Select(l => (DateTime?)l.EndDate)
                    .FirstOrDefault(),
                HasDraftOrPendingLease = u.Leases.Any(l =>
                    l.Status == LeaseStatus.Draft || l.Status == LeaseStatus.PendingSignature),
                DocsCount = _db.StoredFiles.Count(f =>
                    f.PortfolioId == portfolioId
                    && f.EntityId != null
                    && (
                        (f.EntityType == "Unit" && f.EntityId == u.Id)
                        || (f.EntityType == "Lease" && _db.Leases.Any(l =>
                            l.PortfolioId == portfolioId
                            && l.UnitId == u.Id
                            && l.Id == f.EntityId.Value))
                        || (f.EntityType == "Payment" && _db.Payments.Any(p =>
                            p.PortfolioId == portfolioId
                            && p.Id == f.EntityId.Value
                            && _db.Leases.Any(l =>
                                l.PortfolioId == portfolioId
                                && l.UnitId == u.Id
                                && l.Id == p.LeaseId)))
                        || (f.EntityType == "Expense" && _db.Expenses.Any(e =>
                            e.PortfolioId == portfolioId
                            && e.Id == f.EntityId.Value
                            && (e.UnitId == u.Id
                                || (e.WorkOrderId != null && _db.WorkOrders.Any(w =>
                                    w.PortfolioId == portfolioId
                                    && w.UnitId == u.Id
                                    && w.Id == e.WorkOrderId.Value)))))
                        || (f.EntityType == "WorkOrder" && _db.WorkOrders.Any(w =>
                            w.PortfolioId == portfolioId
                            && w.UnitId == u.Id
                            && w.Id == f.EntityId.Value))
                        || (f.EntityType == "Inspection" && _db.Inspections.Any(i =>
                            i.PortfolioId == portfolioId
                            && i.UnitId == u.Id
                            && i.Id == f.EntityId.Value)))),
            })
            .ToListAsync(ct);

        return new UnitHealthListResponse
        {
            Items = rows.Select(r => new UnitHealthResponse
            {
                Id = r.Id,
                PropertyId = r.PropertyId,
                PropertyName = r.PropertyName,
                UnitNumber = r.UnitNumber,
                Status = r.Status.ToString(),
                MarketRent = r.MarketRent,
                OpenWorkOrderCount = r.OpenWorkOrderCount,
                LeaseEndsInDays = r.CurrentLeaseEndDate is { } end
                    ? Math.Max(0, (int)Math.Ceiling((end - now).TotalDays))
                    : null,
                DocsNeedingReviewCount = r.DocsCount,
                SimpleStage = ComputeSimpleStage(
                    r.Status,
                    r.CurrentLeaseStatus,
                    r.CurrentLeaseEndDate,
                    r.HasDraftOrPendingLease,
                    now),
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static UnitHealthListQuery ToUnitHealthListQuery(ListQuery query, int? propertyId) => new()
    {
        Skip = query.Skip,
        Take = query.Take,
        Search = query.Search,
        Sort = query.Sort,
        PropertyId = propertyId,
    };

    /// <summary>
    /// Simplified list badge (NOT the full 9-stage detail derivation): a cheap label from the unit's
    /// occupancy status + current-lease status, formatted from already-projected scalars (no extra query).
    /// </summary>
    private static string ComputeSimpleStage(
        UnitStatus status, LeaseStatus? currentLeaseStatus, DateTime? currentLeaseEnd, bool hasDraftOrPending, DateTime now)
    {
        if (status == UnitStatus.Offline)
        {
            return "Turnover";
        }

        if (currentLeaseStatus is LeaseStatus.Active or LeaseStatus.NoticeGiven)
        {
            if (currentLeaseStatus == LeaseStatus.NoticeGiven)
            {
                return "Move-Out";
            }

            if (currentLeaseEnd is { } end && end >= now && end <= now.AddDays(90))
            {
                return "Renewal";
            }

            return "Active";
        }

        if (hasDraftOrPending)
        {
            return "Lease";
        }

        return "Vacant";
    }

    public async Task<UnitResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Units
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);

        return entity == null ? null : UnitResponse.FromEntity(entity);
    }

    public async Task<UnitResponse?> CreateAsync(int portfolioId, CreateUnitRequest request, CancellationToken ct = default)
    {
        // Verify the target property exists within the caller's portfolio before attaching the unit.
        var propertyInScope = await _db.Properties
            .AnyAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct);
        if (!propertyInScope)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Unit
        {
            PropertyId = request.PropertyId,
            UnitNumber = request.UnitNumber,
            FloorPlan = request.FloorPlan,
            Bedrooms = request.Bedrooms,
            Bathrooms = request.Bathrooms,
            SquareFeet = request.SquareFeet,
            MarketRent = request.MarketRent,
            Status = request.Status,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Units.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            EntityType,
            entity.Id,
            AuditLogOperation.Created,
            newValues: Snapshot(entity),
            changeReason: $"Unit {entity.UnitNumber} created",
            ct: ct);

        var response = UnitResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<UnitResponse?> UpdateAsync(int portfolioId, int id, UpdateUnitRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Units
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();

        ApplyStringIfChanged(request.UnitNumber, entity.UnitNumber, "UnitNumber", v => entity.UnitNumber = v);
        ApplyStringIfChanged(request.FloorPlan, entity.FloorPlan, "FloorPlan", v => entity.FloorPlan = v);
        ApplyValueIfChanged(request.Bedrooms, entity.Bedrooms, "Bedrooms", v => entity.Bedrooms = v);
        ApplyValueIfChanged(request.Bathrooms, entity.Bathrooms, "Bathrooms", v => entity.Bathrooms = v);
        ApplyNullableValueIfChanged(request.SquareFeet, entity.SquareFeet, "SquareFeet", v => entity.SquareFeet = v);
        ApplyValueIfChanged(request.MarketRent, entity.MarketRent, "MarketRent", v => entity.MarketRent = v);
        ApplyValueIfChanged(request.Status, entity.Status, "Status", v => entity.Status = v);
        ApplyStringIfChanged(request.Notes, entity.Notes, "Notes", v => entity.Notes = v);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (oldValues.Count > 0)
        {
            await _audit.LogAsync(
                portfolioId,
                EntityType,
                entity.Id,
                AuditLogOperation.Updated,
                oldValues: Serialize(oldValues),
                newValues: Serialize(newValues),
                changeReason: $"Unit {entity.UnitNumber} updated",
                ct: ct);
        }

        var response = UnitResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;

        void ApplyStringIfChanged(string? requested, string? current, string field, Action<string> apply)
        {
            if (requested is null || string.Equals(requested, current, StringComparison.Ordinal))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested;
            apply(requested);
        }

        void ApplyValueIfChanged<T>(T? requested, T current, string field, Action<T> apply)
            where T : struct
        {
            if (!requested.HasValue || EqualityComparer<T>.Default.Equals(requested.Value, current))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested.Value;
            apply(requested.Value);
        }

        void ApplyNullableValueIfChanged<T>(T? requested, T? current, string field, Action<T?> apply)
            where T : struct
        {
            if (!requested.HasValue || EqualityComparer<T?>.Default.Equals(requested, current))
            {
                return;
            }

            oldValues[field] = current;
            newValues[field] = requested.Value;
            apply(requested.Value);
        }
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Units
            .FirstOrDefaultAsync(u => u.Id == id && u.Property != null && u.Property.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        entity.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            EntityType,
            id,
            AuditLogOperation.Deleted,
            oldValues: Snapshot(entity),
            changeReason: $"Unit {entity.UnitNumber} deleted",
            ct: ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private static string Snapshot(Unit entity) => Serialize(new Dictionary<string, object?>
    {
        ["PropertyId"] = entity.PropertyId,
        ["UnitNumber"] = entity.UnitNumber,
        ["FloorPlan"] = entity.FloorPlan,
        ["Bedrooms"] = entity.Bedrooms,
        ["Bathrooms"] = entity.Bathrooms,
        ["SquareFeet"] = entity.SquareFeet,
        ["MarketRent"] = entity.MarketRent,
        ["Status"] = entity.Status,
        ["Notes"] = entity.Notes,
    });

    private static string Serialize(Dictionary<string, object?> values) => JsonSerializer.Serialize(values);
}
