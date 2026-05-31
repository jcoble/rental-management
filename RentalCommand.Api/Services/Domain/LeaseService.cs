using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseService"/>
public class LeaseService : ILeaseService
{
    private const string EntityType = "Lease";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public LeaseService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<LeaseResponse>> ListAsync(int portfolioId, int? tenantId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId);

        if (tenantId.HasValue)
        {
            q = q.Where(l => l.TenantId == tenantId.Value);
        }

        if (propertyId.HasValue)
        {
            q = q.Where(l => l.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(l => EF.Functions.ILike(l.LeaseNumber, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "leasenumber" => query.SortDescending ? q.OrderByDescending(l => l.LeaseNumber) : q.OrderBy(l => l.LeaseNumber),
            "status" => query.SortDescending ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "startdate" => query.SortDescending ? q.OrderByDescending(l => l.StartDate) : q.OrderBy(l => l.StartDate),
            "enddate" => query.SortDescending ? q.OrderByDescending(l => l.EndDate) : q.OrderBy(l => l.EndDate),
            "monthlyrent" => query.SortDescending ? q.OrderByDescending(l => l.MonthlyRent) : q.OrderBy(l => l.MonthlyRent),
            "updatedat" => query.SortDescending ? q.OrderByDescending(l => l.UpdatedAt) : q.OrderBy(l => l.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(l => l.CreatedAt) : q.OrderBy(l => l.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(LeaseResponse.FromEntity).ToList();
    }

    public async Task<LeaseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);

        return entity == null ? null : LeaseResponse.FromEntity(entity);
    }

    public async Task<LeaseResponse?> CreateAsync(int portfolioId, CreateLeaseRequest request, CancellationToken ct = default)
    {
        // Verify the referenced property, unit, and tenant all live in the caller's portfolio.
        var propertyInScope = await _db.Properties
            .AnyAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct);
        if (!propertyInScope)
        {
            return null;
        }

        var unitInScope = await _db.Units
            .AnyAsync(u => u.Id == request.UnitId && u.PropertyId == request.PropertyId, ct);
        if (!unitInScope)
        {
            return null;
        }

        var tenantInScope = await _db.Tenants
            .AnyAsync(t => t.Id == request.TenantId && t.PortfolioId == portfolioId, ct);
        if (!tenantInScope)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Lease
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            TenantId = request.TenantId,
            LeaseNumber = request.LeaseNumber,
            Status = request.Status,
            StartDate = request.StartDate.ToUtc(),
            EndDate = request.EndDate.ToUtc(),
            MoveInDate = request.MoveInDate.ToUtc(),
            MoveOutDate = request.MoveOutDate.ToUtc(),
            MonthlyRent = request.MonthlyRent,
            SecurityDeposit = request.SecurityDeposit,
            LateFeeAmount = request.LateFeeAmount,
            RentDueDay = request.RentDueDay,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Leases.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = LeaseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<LeaseResponse?> UpdateAsync(int portfolioId, int id, UpdateLeaseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.LeaseNumber != null) entity.LeaseNumber = request.LeaseNumber;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.EndDate.HasValue) entity.EndDate = request.EndDate.Value.ToUtc();
        if (request.MoveInDate.HasValue) entity.MoveInDate = request.MoveInDate.ToUtc();
        if (request.MoveOutDate.HasValue) entity.MoveOutDate = request.MoveOutDate.ToUtc();
        if (request.MonthlyRent.HasValue) entity.MonthlyRent = request.MonthlyRent.Value;
        if (request.SecurityDeposit.HasValue) entity.SecurityDeposit = request.SecurityDeposit.Value;
        if (request.LateFeeAmount.HasValue) entity.LateFeeAmount = request.LateFeeAmount.Value;
        if (request.RentDueDay.HasValue) entity.RentDueDay = request.RentDueDay.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = LeaseResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == id && l.PortfolioId == portfolioId, ct);
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
