using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPropertyService"/>
public class PropertyService : IPropertyService
{
    private const string EntityType = "Property";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public PropertyService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<PropertyResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(p =>
                EF.Functions.ILike(p.Name, $"%{term}%") ||
                EF.Functions.ILike(p.AddressLine1, $"%{term}%") ||
                EF.Functions.ILike(p.City, $"%{term}%") ||
                EF.Functions.ILike(p.State, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name),
            "city" => query.SortDescending ? q.OrderByDescending(p => p.City) : q.OrderBy(p => p.City),
            "status" => query.SortDescending ? q.OrderByDescending(p => p.Status) : q.OrderBy(p => p.Status),
            "updatedat" => query.SortDescending ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        };

        // Unit / occupied counts are computed in SQL as correlated subqueries (p.Units.Count(...))
        // so the database does the aggregation — no Units collection is loaded into memory and
        // counted client-side, and there is no per-row follow-up query (N+1). EF translates each
        // count to a scalar subquery in the single list SELECT.
        var rows = await q
            .Select(p => new ProjectedProperty(
                p,
                p.OwnerEntity != null ? p.OwnerEntity.Name : (p.Owner != null ? p.Owner.Name : null),
                p.Units.Count,
                p.Units.Count(u => u.Status == UnitStatus.Occupied)))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return rows.Select(r => ToResponse(r)).ToList();
    }

    public async Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var row = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id && p.PortfolioId == portfolioId)
            .Select(p => new ProjectedProperty(
                p,
                p.OwnerEntity != null ? p.OwnerEntity.Name : (p.Owner != null ? p.Owner.Name : null),
                p.Units.Count,
                p.Units.Count(u => u.Status == UnitStatus.Occupied)))
            .FirstOrDefaultAsync(ct);

        return row == null ? null : ToResponse(row);
    }

    /// <summary>
    /// Property plus its SQL-computed owner name and unit aggregates. Carrying the entity (rather than
    /// re-listing every scalar in the projection) keeps the read in one SELECT while letting
    /// <see cref="PropertyResponse.FromEntity"/> own the scalar mapping.
    /// </summary>
    private sealed record ProjectedProperty(Property Property, string? OwnerName, int UnitCount, int OccupiedUnits);

    private static PropertyResponse ToResponse(ProjectedProperty row)
    {
        var response = PropertyResponse.FromEntity(row.Property, row.UnitCount, row.OccupiedUnits);
        response.OwnerName = row.OwnerName;
        return response;
    }

    public async Task<PropertyResponse?> CreateAsync(int portfolioId, CreatePropertyRequest request, CancellationToken ct = default)
    {
        // Verify any supplied owner/owner-entity references belong to the caller's portfolio (no cross-tenant linking).
        if (request.OwnerId.HasValue &&
            !await _db.EnsureOwnerInPortfolioAsync(portfolioId, request.OwnerId.Value, ct))
        {
            return null;
        }

        if (request.OwnerEntityId.HasValue &&
            !await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId.Value, ct))
        {
            return null;
        }

        // When no owner is supplied, link the property to the portfolio's primary (self) owner so it shows up
        // in the owners report. If the portfolio has no primary owner yet, leave it null rather than failing.
        var ownerEntityId = request.OwnerEntityId;
        if (!ownerEntityId.HasValue)
        {
            ownerEntityId = await _db.OwnerEntities
                .Where(oe => oe.PortfolioId == portfolioId && oe.IsPrimary)
                .Select(oe => (int?)oe.Id)
                .FirstOrDefaultAsync(ct);
        }

        var now = DateTime.UtcNow;
        var entity = new Property
        {
            PortfolioId = portfolioId,
            OwnerId = request.OwnerId,
            OwnerEntityId = ownerEntityId,
            Name = request.Name,
            PropertyType = request.PropertyType,
            Status = request.Status,
            AddressLine1 = request.AddressLine1,
            AddressLine2 = request.AddressLine2,
            City = request.City,
            State = request.State,
            PostalCode = request.PostalCode,
            YearBuilt = request.YearBuilt,
            ManagementFeePercent = request.ManagementFeePercent,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Properties.Add(entity);
        await _db.SaveChangesAsync(ct);

        // A freshly-created property has no units yet, so the aggregates are 0 — no query needed.
        var response = PropertyResponse.FromEntity(entity);
        response.OwnerName = entity.OwnerEntity?.Name ?? entity.Owner?.Name;
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PropertyResponse?> UpdateAsync(int portfolioId, int id, UpdatePropertyRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Properties
            .Include(p => p.Owner)
            .Include(p => p.OwnerEntity)
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        // Verify any supplied owner/owner-entity references belong to the caller's portfolio (no cross-tenant linking).
        if (request.OwnerId.HasValue &&
            !await _db.EnsureOwnerInPortfolioAsync(portfolioId, request.OwnerId.Value, ct))
        {
            return null;
        }

        if (request.OwnerEntityId.HasValue &&
            !await _db.EnsureOwnerEntityInPortfolioAsync(portfolioId, request.OwnerEntityId.Value, ct))
        {
            return null;
        }

        if (request.OwnerId.HasValue) entity.OwnerId = request.OwnerId;
        if (request.OwnerEntityId.HasValue) entity.OwnerEntityId = request.OwnerEntityId;
        if (request.Name != null) entity.Name = request.Name;
        if (request.PropertyType.HasValue) entity.PropertyType = request.PropertyType.Value;
        if (request.Status.HasValue) entity.Status = request.Status.Value;
        if (request.AddressLine1 != null) entity.AddressLine1 = request.AddressLine1;
        if (request.AddressLine2 != null) entity.AddressLine2 = request.AddressLine2;
        if (request.City != null) entity.City = request.City;
        if (request.State != null) entity.State = request.State;
        if (request.PostalCode != null) entity.PostalCode = request.PostalCode;
        if (request.YearBuilt.HasValue) entity.YearBuilt = request.YearBuilt;
        if (request.ManagementFeePercent.HasValue) entity.ManagementFeePercent = request.ManagementFeePercent;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Re-read the unit aggregates in SQL (single scalar query) so the broadcast row carries the
        // same counts the list/detail show — the edit doesn't change unit membership, but keeping the
        // shape consistent avoids the grid flashing 0s on a live update.
        var counts = await _db.Properties
            .AsNoTracking()
            .Where(p => p.Id == id && p.PortfolioId == portfolioId)
            .Select(p => new { UnitCount = p.Units.Count, Occupied = p.Units.Count(u => u.Status == UnitStatus.Occupied) })
            .FirstOrDefaultAsync(ct);

        var response = PropertyResponse.FromEntity(entity, counts?.UnitCount ?? 0, counts?.Occupied ?? 0);
        response.OwnerName = entity.OwnerEntity?.Name ?? entity.Owner?.Name;
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Properties
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);
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
