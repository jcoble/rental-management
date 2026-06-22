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

    public UnitService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
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
                // The active lease's end date + a flag, taken from the latest-ending active lease.
                ActiveLeaseEndDate = u.Leases
                    .Where(l => l.Status == LeaseStatus.Active)
                    .OrderByDescending(l => l.EndDate)
                    .Select(l => (DateTime?)l.EndDate)
                    .FirstOrDefault(),
                ActiveLeaseNoticeGiven = u.Leases.Any(l => l.Status == LeaseStatus.NoticeGiven),
                HasActiveLease = u.Leases.Any(l => l.Status == LeaseStatus.Active),
                HasDraftOrPendingLease = u.Leases.Any(l =>
                    l.Status == LeaseStatus.Draft || l.Status == LeaseStatus.PendingSignature),
                DocsCount = _db.StoredFiles.Count(f =>
                    f.PortfolioId == portfolioId && f.EntityType == "Unit" && f.EntityId == u.Id),
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
                LeaseEndsInDays = r.ActiveLeaseEndDate is { } end
                    ? Math.Max(0, (int)Math.Ceiling((end - now).TotalDays))
                    : null,
                DocsNeedingReviewCount = r.DocsCount,
                SimpleStage = ComputeSimpleStage(r.Status, r.HasActiveLease, r.ActiveLeaseNoticeGiven, r.ActiveLeaseEndDate, r.HasDraftOrPendingLease, now),
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
    /// occupancy status + active-lease status, formatted from already-projected scalars (no extra query).
    /// </summary>
    private static string ComputeSimpleStage(
        UnitStatus status, bool hasActiveLease, bool noticeGiven, DateTime? activeLeaseEnd, bool hasDraftOrPending, DateTime now)
    {
        if (status == UnitStatus.Offline)
        {
            return "Turnover";
        }

        if (hasActiveLease)
        {
            if (noticeGiven)
            {
                return "Move-Out";
            }

            if (activeLeaseEnd is { } end && end >= now && end <= now.AddDays(90))
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

        if (request.UnitNumber != null) entity.UnitNumber = request.UnitNumber;
        if (request.FloorPlan != null) entity.FloorPlan = request.FloorPlan;
        if (request.Bedrooms.HasValue) entity.Bedrooms = request.Bedrooms.Value;
        if (request.Bathrooms.HasValue) entity.Bathrooms = request.Bathrooms.Value;
        if (request.SquareFeet.HasValue) entity.SquareFeet = request.SquareFeet;
        if (request.MarketRent.HasValue) entity.MarketRent = request.MarketRent.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = UnitResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
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

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
