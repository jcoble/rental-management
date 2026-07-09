using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ICapitalAssetService"/>
public class CapitalAssetService : ICapitalAssetService
{
    private const string EntityType = "CapitalAsset";
    private const string ExpenseEntityType = "Expense";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public CapitalAssetService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<CapitalAssetResponse>> ListAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<CapitalAssetListResponse> ListPageAsync(
        int portfolioId, CapitalAssetListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, query);
        var totalCount = await filtered.CountAsync(ct);
        var depreciationYear = query.Year ?? _timeProvider.UtcNow().Year;

        var items = await ProjectResponse(ApplySort(filtered, query), depreciationYear)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        PopulateDepreciation(items, depreciationYear);

        return new CapitalAssetListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<CapitalAssetResponse?> GetAsync(
        int portfolioId, int id, int? depreciationYear = null, CancellationToken ct = default)
    {
        var year = depreciationYear ?? _timeProvider.UtcNow().Year;
        var response = await ProjectResponse(_db.CapitalAssets
                .AsNoTracking()
                .Where(a => a.Id == id && a.PortfolioId == portfolioId), year)
            .FirstOrDefaultAsync(ct);
        if (response is not null)
            PopulateDepreciation([response], year);

        return response;
    }

    public async Task<CapitalAssetResponse?> CreateAsync(
        int portfolioId, CreateCapitalAssetRequest request, CancellationToken ct = default)
    {
        if (!await ValidatePropertyUnitAsync(portfolioId, request.PropertyId, request.UnitId, ct))
            return null;

        var now = _timeProvider.UtcNow();
        var entity = new CapitalAsset
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            UnitId = request.UnitId,
            Description = request.Description.Trim(),
            CostBasis = request.CostBasis,
            InServiceDate = request.InServiceDate.ToUtc(),
            Method = request.Method,
            RecoveryYears = request.RecoveryYears,
            Convention = request.Convention,
            AccumulatedDepreciation = request.AccumulatedDepreciation,
            DisposedOnDate = request.DisposedOnDate.ToUtc(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.CapitalAssets.Add(entity);
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct: ct) ?? CapitalAssetResponse.FromEntity(entity, now.Year);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<CapitalAssetResponse?> UpdateAsync(
        int portfolioId, int id, UpdateCapitalAssetRequest request, CancellationToken ct = default)
    {
        var entity = await _db.CapitalAssets
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity is null)
            return null;

        var propertyId = request.PropertyId ?? entity.PropertyId;
        int? unitId = entity.UnitId;
        if (request.ClearUnit == true)
            unitId = null;
        if (request.UnitId.HasValue)
            unitId = request.UnitId.Value;

        if (!await ValidatePropertyUnitAsync(portfolioId, propertyId, unitId, ct))
            return null;

        entity.PropertyId = propertyId;
        entity.UnitId = unitId;
        if (request.Description != null) entity.Description = request.Description.Trim();
        if (request.CostBasis.HasValue) entity.CostBasis = request.CostBasis.Value;
        if (request.InServiceDate.HasValue) entity.InServiceDate = request.InServiceDate.Value.ToUtc();
        if (request.Method.HasValue) entity.Method = request.Method.Value;
        if (request.RecoveryYears.HasValue) entity.RecoveryYears = request.RecoveryYears.Value;
        if (request.Convention.HasValue) entity.Convention = request.Convention.Value;
        if (request.AccumulatedDepreciation.HasValue) entity.AccumulatedDepreciation = request.AccumulatedDepreciation.Value;
        if (request.ClearDisposedOnDate == true) entity.DisposedOnDate = null;
        else if (request.DisposedOnDate.HasValue) entity.DisposedOnDate = request.DisposedOnDate.Value.ToUtc();
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct: ct) ?? CapitalAssetResponse.FromEntity(entity, entity.UpdatedAt.Year);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<CapitalAssetResponse?> CapitalizeExpenseAsync(
        int portfolioId, int expenseId, CapitalizeExpenseRequest request, CancellationToken ct = default)
    {
        var expense = await _db.Expenses
            .FirstOrDefaultAsync(e => e.Id == expenseId && e.PortfolioId == portfolioId, ct);
        if (expense is null || expense.PropertyId is null || expense.CapitalizedAssetId is not null)
            return null;

        var now = _timeProvider.UtcNow();
        var asset = new CapitalAsset
        {
            PortfolioId = portfolioId,
            PropertyId = expense.PropertyId.Value,
            UnitId = expense.UnitId,
            SourceExpenseId = expense.Id,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? expense.Description
                : request.Description.Trim(),
            CostBasis = expense.Amount,
            InServiceDate = request.InServiceDate.ToUtc(),
            Method = request.Method,
            RecoveryYears = request.RecoveryYears,
            Convention = request.Convention,
            AccumulatedDepreciation = 0m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.CapitalAssets.Add(asset);
        await _db.SaveChangesAsync(ct);

        expense.CapitalizedAssetId = asset.Id;
        expense.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var response = await GetAsync(portfolioId, asset.Id, ct: ct) ?? CapitalAssetResponse.FromEntity(asset, now.Year);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, asset.Id, response, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, ExpenseEntityType, expense.Id, ExpenseResponse.FromEntity(expense), ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.CapitalAssets
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity is null)
            return false;

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;

        var linkedExpenses = await _db.Expenses
            .Where(e => e.PortfolioId == portfolioId && e.CapitalizedAssetId == id)
            .ToListAsync(ct);
        foreach (var expense in linkedExpenses)
        {
            expense.CapitalizedAssetId = null;
            expense.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        foreach (var expense in linkedExpenses)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, ExpenseEntityType, expense.Id, ExpenseResponse.FromEntity(expense), ct);

        return true;
    }

    public DepreciationResult AnnualDepreciationForYear(CapitalAsset asset, int year)
    {
        return DepreciationCalculator.AnnualForYear(
            asset.CostBasis,
            asset.InServiceDate,
            asset.Method,
            asset.RecoveryYears,
            asset.Convention,
            asset.AccumulatedDepreciation,
            year);
    }

    private IQueryable<CapitalAsset> BuildListQuery(int portfolioId, CapitalAssetListQuery query)
    {
        var q = _db.CapitalAssets
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

        if (query.PropertyId.HasValue)
            q = q.Where(a => a.PropertyId == query.PropertyId.Value);

        if (query.UnitId.HasValue)
            q = q.Where(a => a.UnitId == query.UnitId.Value);

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(a => a.InServiceDate >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(a => a.InServiceDate < toUtcExclusive);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                EF.Functions.ILike(a.Description, $"%{term}%") ||
                EF.Functions.ILike(a.Property!.Name, $"%{term}%") ||
                (a.Unit != null && EF.Functions.ILike(a.Unit.UnitNumber, $"%{term}%")) ||
                (a.SourceExpense != null && EF.Functions.ILike(a.SourceExpense.Description, $"%{term}%")));
        }

        return q;
    }

    private static IQueryable<CapitalAsset> ApplySort(IQueryable<CapitalAsset> q, ListQuery query)
    {
        var ordered = query.SortField switch
        {
            "description" => query.SortDescending ? q.OrderByDescending(a => a.Description) : q.OrderBy(a => a.Description),
            "property" or "propertyname" => query.SortDescending ? q.OrderByDescending(a => a.Property!.Name) : q.OrderBy(a => a.Property!.Name),
            "unit" or "unitnumber" => query.SortDescending ? q.OrderByDescending(a => a.Unit!.UnitNumber) : q.OrderBy(a => a.Unit!.UnitNumber),
            "costbasis" => query.SortDescending ? q.OrderByDescending(a => a.CostBasis) : q.OrderBy(a => a.CostBasis),
            "method" => query.SortDescending ? q.OrderByDescending(a => a.Method) : q.OrderBy(a => a.Method),
            "recoveryyears" => query.SortDescending ? q.OrderByDescending(a => a.RecoveryYears) : q.OrderBy(a => a.RecoveryYears),
            "convention" => query.SortDescending ? q.OrderByDescending(a => a.Convention) : q.OrderBy(a => a.Convention),
            "accumulateddepreciation" => query.SortDescending ? q.OrderByDescending(a => a.AccumulatedDepreciation) : q.OrderBy(a => a.AccumulatedDepreciation),
            "updatedat" => query.SortDescending ? q.OrderByDescending(a => a.UpdatedAt) : q.OrderBy(a => a.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
            "inservicedate" => query.SortDescending ? q.OrderByDescending(a => a.InServiceDate) : q.OrderBy(a => a.InServiceDate),
            _ => q.OrderByDescending(a => a.InServiceDate),
        };

        return ordered.ThenByDescending(a => a.Id);
    }

    private IQueryable<CapitalAssetResponse> ProjectResponse(IQueryable<CapitalAsset> query, int depreciationYear)
    {
        return query.Select(a => new CapitalAssetResponse
        {
            Id = a.Id,
            PortfolioId = a.PortfolioId,
            PropertyId = a.PropertyId,
            PropertyName = a.Property!.Name,
            UnitId = a.UnitId,
            UnitNumber = a.Unit == null ? null : a.Unit.UnitNumber,
            SourceExpenseId = a.SourceExpenseId,
            SourceExpenseDescription = a.SourceExpense == null ? null : a.SourceExpense.Description,
            Description = a.Description,
            CostBasis = a.CostBasis,
            InServiceDate = a.InServiceDate,
            Method = a.Method,
            RecoveryYears = a.RecoveryYears,
            Convention = a.Convention,
            AccumulatedDepreciation = a.AccumulatedDepreciation,
            DisposedOnDate = a.DisposedOnDate,
            DepreciationYear = depreciationYear,
            CreatedAt = a.CreatedAt,
            UpdatedAt = a.UpdatedAt,
        });
    }

    private static void PopulateDepreciation(IReadOnlyCollection<CapitalAssetResponse> items, int year)
    {
        foreach (var item in items)
        {
            var annual = DepreciationCalculator.AnnualForYear(
                item.CostBasis,
                item.InServiceDate,
                item.Method,
                item.RecoveryYears,
                item.Convention,
                item.AccumulatedDepreciation,
                year);

            item.DepreciationYear = year;
            item.AnnualDepreciation = annual.Amount;
            item.IsFirstYearEstimate = annual.IsFirstYearEstimate;
        }
    }

    private async Task<bool> ValidatePropertyUnitAsync(
        int portfolioId, int propertyId, int? unitId, CancellationToken ct)
    {
        if (!await _db.EnsurePropertyInPortfolioAsync(portfolioId, propertyId, ct))
            return false;

        return unitId is null ||
            await _db.EnsureUnitInPortfolioAsync(portfolioId, unitId.Value, propertyId, ct);
    }
}
