using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IRecurringExpenseService"/>
public class RecurringExpenseService : IRecurringExpenseService
{
    private const string EntityType = "RecurringExpense";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public RecurringExpenseService(RentalCommandDbContext db, IDataUpdateService dataUpdate, TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<RecurringExpenseResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.RecurringExpenses
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId);

        if (propertyId.HasValue)
            q = q.Where(t => t.PropertyId == propertyId.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t => EF.Functions.ILike(t.Description, $"%{term}%"));
        }

        q = query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(t => t.Description) : q.OrderBy(t => t.Description),
            "amount" => query.SortDescending ? q.OrderByDescending(t => t.Amount) : q.OrderBy(t => t.Amount),
            "category" => query.SortDescending ? q.OrderByDescending(t => t.Category) : q.OrderBy(t => t.Category),
            "nextrundate" => query.SortDescending ? q.OrderByDescending(t => t.NextRunDate) : q.OrderBy(t => t.NextRunDate),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAt) : q.OrderBy(t => t.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(t => new { Template = t, PropertyName = t.Property != null ? t.Property.Name : null })
            .ToListAsync(ct);

        return items.Select(x =>
        {
            var r = RecurringExpenseResponse.FromEntity(x.Template);
            r.PropertyName = x.PropertyName;
            return r;
        }).ToList();
    }

    public async Task<RecurringExpenseResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.RecurringExpenses
            .AsNoTracking()
            .Include(t => t.Property)
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);

        return entity == null ? null : RecurringExpenseResponse.FromEntity(entity);
    }

    public async Task<RecurringExpenseResponse?> CreateAsync(int portfolioId, CreateRecurringExpenseRequest request, CancellationToken ct = default)
    {
        // IDOR guards: any supplied property/unit must belong to this portfolio.
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
            return null;

        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, request.PropertyId, ct))
            return null;

        var now = _timeProvider.UtcNow();
        var start = request.StartDate.ToUtc();
        var entity = new RecurringExpense
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            Category = request.Category,
            Description = request.Description,
            Amount = request.Amount,
            Frequency = request.Frequency,
            StartDate = start,
            NextRunDate = (request.NextRunDate?.ToUtc()) ?? start,
            Active = request.Active,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RecurringExpenses.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await BuildResponseAsync(entity, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<RecurringExpenseResponse?> UpdateAsync(int portfolioId, int id, UpdateRecurringExpenseRequest request, CancellationToken ct = default)
    {
        var entity = await _db.RecurringExpenses
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        // Validate FK changes in-portfolio. Use the effective property id for the unit check.
        if (request.PropertyId.HasValue &&
            !await _db.EnsurePropertyInPortfolioAsync(portfolioId, request.PropertyId.Value, ct))
            return null;

        var effectivePropertyId = request.PropertyId ?? entity.PropertyId;
        if (request.UnitId.HasValue &&
            !await _db.EnsureUnitInPortfolioAsync(portfolioId, request.UnitId.Value, effectivePropertyId, ct))
            return null;

        if (request.PropertyId.HasValue) entity.PropertyId = request.PropertyId;
        if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
        if (request.Category.HasValue) entity.Category = request.Category.Value;
        if (request.Description != null) entity.Description = request.Description;
        if (request.Amount.HasValue) entity.Amount = request.Amount.Value;
        if (request.Frequency.HasValue) entity.Frequency = request.Frequency.Value;
        if (request.StartDate.HasValue) entity.StartDate = request.StartDate.Value.ToUtc();
        if (request.NextRunDate.HasValue) entity.NextRunDate = request.NextRunDate.Value.ToUtc();
        if (request.Active.HasValue) entity.Active = request.Active.Value;
        if (request.Notes != null) entity.Notes = request.Notes;
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await BuildResponseAsync(entity, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.RecurringExpenses
            .FirstOrDefaultAsync(t => t.Id == id && t.PortfolioId == portfolioId, ct);
        if (entity == null)
            return false;

        entity.DeletedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private async Task<RecurringExpenseResponse> BuildResponseAsync(RecurringExpense entity, CancellationToken ct)
    {
        var response = RecurringExpenseResponse.FromEntity(entity);
        if (entity.PropertyId.HasValue)
        {
            response.PropertyName = await _db.Properties
                .AsNoTracking()
                .Where(p => p.Id == entity.PropertyId.Value)
                .Select(p => p.Name)
                .FirstOrDefaultAsync(ct);
        }
        return response;
    }
}
