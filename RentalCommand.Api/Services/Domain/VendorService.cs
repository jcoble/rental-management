using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IVendorService"/>
public class VendorService : IVendorService
{
    private const string EntityType = "Vendor";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public VendorService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<VendorResponse>> ListAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(v =>
                EF.Functions.ILike(v.Name, $"%{term}%") ||
                EF.Functions.ILike(v.ServiceType, $"%{term}%") ||
                (v.Email != null && EF.Functions.ILike(v.Email, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(v => v.Name) : q.OrderBy(v => v.Name),
            "servicetype" => query.SortDescending ? q.OrderByDescending(v => v.ServiceType) : q.OrderBy(v => v.ServiceType),
            "updatedat" => query.SortDescending ? q.OrderByDescending(v => v.UpdatedAt) : q.OrderBy(v => v.UpdatedAt),
            _ => query.SortDescending ? q.OrderByDescending(v => v.CreatedAt) : q.OrderBy(v => v.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(VendorResponse.FromEntity).ToList();
    }

    public async Task<VendorResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);

        return entity == null ? null : VendorResponse.FromEntity(entity);
    }

    public async Task<VendorResponse> CreateAsync(int portfolioId, CreateVendorRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = new Vendor
        {
            PortfolioId = portfolioId,
            Name = request.Name,
            ServiceType = request.ServiceType,
            Email = request.Email,
            Phone = request.Phone,
            TaxId = request.TaxId,
            Is1099Eligible = request.Is1099Eligible,
            W9OnFile = request.W9OnFile,
            Preferred = request.Preferred,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Vendors.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<VendorResponse?> UpdateAsync(int portfolioId, int id, UpdateVendorRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.Name != null) entity.Name = request.Name;
        if (request.ServiceType != null) entity.ServiceType = request.ServiceType;
        if (request.Email != null) entity.Email = request.Email;
        if (request.Phone != null) entity.Phone = request.Phone;
        if (request.TaxId != null) entity.TaxId = request.TaxId;
        if (request.Is1099Eligible.HasValue) entity.Is1099Eligible = request.Is1099Eligible.Value;
        if (request.W9OnFile.HasValue) entity.W9OnFile = request.W9OnFile.Value;
        if (request.Preferred.HasValue) entity.Preferred = request.Preferred.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = VendorResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Vendors
            .FirstOrDefaultAsync(v => v.Id == id && v.PortfolioId == portfolioId, ct);
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
