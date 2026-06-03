using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
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

        var items = await q
            .Include(p => p.Owner)
            .Include(p => p.OwnerEntity)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(PropertyResponse.FromEntity).ToList();
    }

    public async Task<PropertyResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Properties
            .AsNoTracking()
            .Include(p => p.Owner)
            .Include(p => p.OwnerEntity)
            .FirstOrDefaultAsync(p => p.Id == id && p.PortfolioId == portfolioId, ct);

        return entity == null ? null : PropertyResponse.FromEntity(entity);
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

        var now = DateTime.UtcNow;
        var entity = new Property
        {
            PortfolioId = portfolioId,
            OwnerId = request.OwnerId,
            OwnerEntityId = request.OwnerEntityId,
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

        var response = PropertyResponse.FromEntity(entity);
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

        var response = PropertyResponse.FromEntity(entity);
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
