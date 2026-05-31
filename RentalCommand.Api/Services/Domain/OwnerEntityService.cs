using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOwnerEntityService"/>
public class OwnerEntityService : IOwnerEntityService
{
    private const string EntityType = "OwnerEntity";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public OwnerEntityService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<OwnerEntityResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.OwnerEntities
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(o =>
                EF.Functions.ILike(o.Name, $"%{term}%") ||
                (o.TaxId != null && EF.Functions.ILike(o.TaxId, $"%{term}%")) ||
                (o.Phone != null && EF.Functions.ILike(o.Phone, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(o => o.Name) : q.OrderBy(o => o.Name),
            "type" => query.SortDescending ? q.OrderByDescending(o => o.OwnerEntityType) : q.OrderBy(o => o.OwnerEntityType),
            "updatedat" => query.SortDescending ? q.OrderByDescending(o => o.UpdatedAt) : q.OrderBy(o => o.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(o => o.CreatedAt) : q.OrderBy(o => o.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(OwnerEntityResponse.FromEntity).ToList();
    }

    public async Task<OwnerEntityResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.OwnerEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);

        return entity == null ? null : OwnerEntityResponse.FromEntity(entity);
    }

    public async Task<OwnerEntityResponse> CreateAsync(int portfolioId, CreateOwnerEntityRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = new OwnerEntity
        {
            PortfolioId = portfolioId,
            OwnerEntityType = request.OwnerEntityType,
            Name = request.Name,
            TaxId = request.TaxId,
            Address = request.Address,
            Phone = request.Phone,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.OwnerEntities.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = OwnerEntityResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<OwnerEntityResponse?> UpdateAsync(int portfolioId, int id, UpdateOwnerEntityRequest request, CancellationToken ct = default)
    {
        var entity = await _db.OwnerEntities
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.OwnerEntityType.HasValue) entity.OwnerEntityType = request.OwnerEntityType.Value;
        if (request.Name != null) entity.Name = request.Name;
        if (request.TaxId != null) entity.TaxId = request.TaxId;
        if (request.Address != null) entity.Address = request.Address;
        if (request.Phone != null) entity.Phone = request.Phone;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = OwnerEntityResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.OwnerEntities
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);
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
