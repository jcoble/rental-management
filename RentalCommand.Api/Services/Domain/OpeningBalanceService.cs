using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IOpeningBalanceService"/>
public class OpeningBalanceService : IOpeningBalanceService
{
    private const string EntityType = "OpeningBalance";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public OpeningBalanceService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<OpeningBalanceResponse>> ListAsync(int portfolioId, int? leaseId, CancellationToken ct = default)
    {
        var q = _db.OpeningBalances
            .AsNoTracking()
            .Where(o => o.PortfolioId == portfolioId);

        if (leaseId.HasValue)
        {
            q = q.Where(o => o.LeaseId == leaseId.Value);
        }

        var items = await q
            .OrderByDescending(o => o.AsOfDate)
            .ThenByDescending(o => o.Id)
            .ToListAsync(ct);

        return items.Select(OpeningBalanceResponse.FromEntity).ToList();
    }

    public async Task<OpeningBalanceResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.OpeningBalances
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);

        return entity == null ? null : OpeningBalanceResponse.FromEntity(entity);
    }

    public async Task<(CreateOpeningBalanceResult Result, OpeningBalanceResponse? Response)> CreateAsync(
        int portfolioId, CreateOpeningBalanceRequest request, CancellationToken ct = default)
    {
        // The referenced lease must live in the caller's portfolio (IDOR guard).
        if (!await _db.EnsureLeaseInPortfolioAsync(portfolioId, request.LeaseId, ct))
        {
            return (CreateOpeningBalanceResult.LeaseNotFound, null);
        }

        // One opening balance per lease — reject a duplicate so the caller PATCHes the existing one
        // instead of silently creating a second carried-over figure.
        var exists = await _db.OpeningBalances
            .AnyAsync(o => o.PortfolioId == portfolioId && o.LeaseId == request.LeaseId, ct);
        if (exists)
        {
            return (CreateOpeningBalanceResult.AlreadyExists, null);
        }

        var now = DateTime.UtcNow;
        var entity = new OpeningBalance
        {
            PortfolioId = portfolioId,
            LeaseId = request.LeaseId,
            Amount = request.Amount,
            AsOfDate = request.AsOfDate.ToUtc(),
            Note = request.Note,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.OpeningBalances.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = OpeningBalanceResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return (CreateOpeningBalanceResult.Created, response);
    }

    public async Task<OpeningBalanceResponse?> UpdateAsync(int portfolioId, int id, UpdateOpeningBalanceRequest request, CancellationToken ct = default)
    {
        var entity = await _db.OpeningBalances
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return null;
        }

        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.AsOfDate.HasValue) entity.AsOfDate = request.AsOfDate.Value.ToUtc();
        if (request.Note != null) entity.Note = request.Note;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = OpeningBalanceResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.OpeningBalances
            .FirstOrDefaultAsync(o => o.Id == id && o.PortfolioId == portfolioId, ct);
        if (entity == null)
        {
            return false;
        }

        // No soft-delete column on OpeningBalance; remove the row outright.
        _db.OpeningBalances.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }
}
