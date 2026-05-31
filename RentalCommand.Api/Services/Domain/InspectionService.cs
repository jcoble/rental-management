using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IInspectionService"/>
public class InspectionService : IInspectionService
{
    private const string EntityType = "Inspection";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public InspectionService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<InspectionResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Inspections
            .AsNoTracking()
            .Where(i => i.PortfolioId == portfolioId);

        if (propertyId.HasValue)
        {
            q = q.Where(i => i.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(i =>
                (i.Outcome != null && EF.Functions.ILike(i.Outcome, $"%{term}%")) ||
                (i.Notes != null && EF.Functions.ILike(i.Notes, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "status" => query.SortDescending ? q.OrderByDescending(i => i.Status) : q.OrderBy(i => i.Status),
            "type" => query.SortDescending ? q.OrderByDescending(i => i.Type) : q.OrderBy(i => i.Type),
            "scheduledfor" => query.SortDescending ? q.OrderByDescending(i => i.ScheduledFor) : q.OrderBy(i => i.ScheduledFor),
            "completedat" => query.SortDescending ? q.OrderByDescending(i => i.CompletedAt) : q.OrderBy(i => i.CompletedAt),
            "updatedat" => query.SortDescending ? q.OrderByDescending(i => i.UpdatedAt) : q.OrderBy(i => i.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(i => i.CreatedAt) : q.OrderBy(i => i.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(i => i.ScheduledFor) : q.OrderBy(i => i.ScheduledFor),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(InspectionResponse.FromEntity).ToList();
    }

    public async Task<InspectionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Inspections
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);

        return entity == null ? null : InspectionResponse.FromEntity(entity);
    }

    public async Task<InspectionResponse?> CreateAsync(int portfolioId, CreateInspectionRequest request, CancellationToken ct = default)
    {
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
        {
            return null;
        }

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var entity = new Inspection
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            LeaseId = request.LeaseId,
            Type = request.Type,
            Status = request.Status,
            ScheduledFor = request.ScheduledFor.ToUtc(),
            CompletedAt = request.CompletedAt.ToUtc(),
            Outcome = request.Outcome,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Inspections.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = InspectionResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<InspectionResponse?> UpdateAsync(int portfolioId, int id, UpdateInspectionRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, entity.PropertyId, ct))
        {
            return null;
        }

        if (request.LeaseId.HasValue &&
            !await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId.Value, ct))
        {
            return null;
        }

        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.LeaseId.HasValue) entity.LeaseId = request.LeaseId;
        if (request.Type.HasValue) entity.Type = request.Type.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.ScheduledFor.HasValue) entity.ScheduledFor = request.ScheduledFor.Value.ToUtc();
        if (request.CompletedAt.HasValue) entity.CompletedAt = request.CompletedAt.ToUtc();
        if (request.Outcome != null) entity.Outcome = request.Outcome;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = InspectionResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Inspections
            .FirstOrDefaultAsync(i => i.Id == id && i.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // No soft-delete column on Inspection; remove the row outright.
        _db.Inspections.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
