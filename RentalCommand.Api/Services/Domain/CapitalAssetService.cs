using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ICapitalAssetService"/>
public class CapitalAssetService : ICapitalAssetService
{
    private const string EntityType = "CapitalAsset";
    private const string ExpenseEntityType = "Expense";
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.ReportsRead, CapabilityKeys.MoneyOwnerReportsRead, CapabilityKeys.MoneyExpensesManage];
    private static readonly string[] WriteCapabilities = [CapabilityKeys.MoneyExpensesManage];

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
        => await ListPageFromQueryAsync(
            _db.CapitalAssets.AsNoTracking().Where(asset => asset.PortfolioId == portfolioId),
            query,
            ct);

    public async Task<IReadOnlyList<CapitalAssetResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, CapitalAssetListQuery query, CancellationToken ct = default)
        => (await ListPageAuthorizedAsync(scope, query, ct)).Items;

    public Task<CapitalAssetListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, CapitalAssetListQuery query, CancellationToken ct = default)
        => ListPageFromQueryAsync(AuthorizedAssets(scope, ReadCapabilities), query, ct);

    private async Task<CapitalAssetListResponse> ListPageFromQueryAsync(
        IQueryable<CapitalAsset> assets,
        CapitalAssetListQuery query,
        CancellationToken ct)
    {
        var filtered = BuildListQuery(assets, query);
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

    public async Task<CapitalAssetResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int? depreciationYear = null,
        CancellationToken ct = default)
    {
        var year = depreciationYear ?? _timeProvider.UtcNow().Year;
        var response = await ProjectResponse(
                AuthorizedAssets(scope, ReadCapabilities).Where(asset => asset.Id == id),
                year)
            .FirstOrDefaultAsync(ct);
        if (response != null)
            PopulateDepreciation([response], year);
        return response;
    }

    public async Task<CapitalAssetResponse?> CreateAsync(
        int portfolioId, CreateCapitalAssetRequest request, CancellationToken ct = default)
        => await CreateCoreAsync(portfolioId, request, broadcast: true, ct);

    public async Task<CapitalAssetResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateCapitalAssetRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(async innerCt =>
        {
            var authorized = await _db.Properties.AsNoTracking()
                .WhereAuthorized(_db, scope, WriteCapabilities, _timeProvider.UtcNow())
                .AnyAsync(property => property.Id == request.PropertyId, innerCt);
            return authorized
                ? await CreateCoreAsync(scope.PortfolioId, request, broadcast: false, innerCt)
                : null;
        }, ct);

        if (response != null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    private async Task<CapitalAssetResponse?> CreateCoreAsync(
        int portfolioId,
        CreateCapitalAssetRequest request,
        bool broadcast,
        CancellationToken ct)
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
        if (broadcast)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<CapitalAssetResponse?> UpdateAsync(
        int portfolioId, int id, UpdateCapitalAssetRequest request, CancellationToken ct = default)
        => await UpdateCoreAsync(
            portfolioId,
            _db.CapitalAssets.Where(asset => asset.PortfolioId == portfolioId),
            id,
            request,
            authorizedProperties: null,
            broadcast: true,
            ct);

    public async Task<CapitalAssetResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateCapitalAssetRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
        {
            var authorizedProperties = _db.Properties.AsNoTracking()
                .WhereAuthorized(_db, scope, WriteCapabilities, _timeProvider.UtcNow());
            return UpdateCoreAsync(
                scope.PortfolioId,
                AuthorizedAssets(scope, WriteCapabilities, tracking: true),
                id,
                request,
                authorizedProperties,
                broadcast: false,
                innerCt);
        }, ct);

        if (response != null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, response.Id, response, ct);
        return response;
    }

    private async Task<CapitalAssetResponse?> UpdateCoreAsync(
        int portfolioId,
        IQueryable<CapitalAsset> assets,
        int id,
        UpdateCapitalAssetRequest request,
        IQueryable<Property>? authorizedProperties,
        bool broadcast,
        CancellationToken ct)
    {
        var entity = await assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (entity is null)
            return null;

        var propertyId = request.PropertyId ?? entity.PropertyId;
        int? unitId = entity.UnitId;
        if (request.ClearUnit == true)
            unitId = null;
        if (request.UnitId.HasValue)
            unitId = request.UnitId.Value;

        if (authorizedProperties != null &&
            !await authorizedProperties.AnyAsync(property => property.Id == propertyId, ct))
            return null;

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
        if (broadcast)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<CapitalAssetResponse?> CapitalizeExpenseAsync(
        int portfolioId, int expenseId, CapitalizeExpenseRequest request, CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(
            innerCt => CapitalizeExpenseCoreAsync(
                portfolioId,
                _db.Expenses.Where(expense => expense.PortfolioId == portfolioId),
                expenseId,
                request,
                innerCt),
            ct);
        await BroadcastCapitalizationAsync(portfolioId, expenseId, response, ct);
        return response;
    }

    public async Task<CapitalAssetResponse?> CapitalizeExpenseAuthorizedAsync(
        WorkspaceReadScope scope,
        int expenseId,
        CapitalizeExpenseRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(innerCt =>
        {
            var authorizedProperties = _db.Properties.AsNoTracking()
                .WhereAuthorized(_db, scope, WriteCapabilities, _timeProvider.UtcNow());
            var expenses = _db.Expenses.Where(expense =>
                expense.PortfolioId == scope.PortfolioId &&
                expense.PropertyId != null &&
                authorizedProperties.Any(property =>
                    property.Id == expense.PropertyId &&
                    property.PortfolioId == expense.PortfolioId));
            return CapitalizeExpenseCoreAsync(
                scope.PortfolioId, expenses, expenseId, request, innerCt);
        }, ct);
        await BroadcastCapitalizationAsync(scope.PortfolioId, expenseId, response, ct);
        return response;
    }

    private async Task<CapitalAssetResponse?> CapitalizeExpenseCoreAsync(
        int portfolioId,
        IQueryable<Expense> expenses,
        int expenseId,
        CapitalizeExpenseRequest request,
        CancellationToken ct)
    {
        var expense = await expenses.FirstOrDefaultAsync(e => e.Id == expenseId, ct);
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

        _db.CapitalAssets.Add(asset);
        await _db.SaveChangesAsync(ct);

        expense.CapitalizedAssetId = asset.Id;
        expense.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, asset.Id, ct: ct) ?? CapitalAssetResponse.FromEntity(asset, now.Year);
        return response;
    }

    private async Task BroadcastCapitalizationAsync(
        int portfolioId,
        int expenseId,
        CapitalAssetResponse? response,
        CancellationToken ct)
    {
        if (response == null)
            return;

        var expense = await _db.Expenses.AsNoTracking()
            .FirstAsync(item => item.Id == expenseId && item.PortfolioId == portfolioId, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, response.Id, response, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, ExpenseEntityType, expense.Id, ExpenseResponse.FromEntity(expense), ct);
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var result = await _db.ExecuteAuthorizedMutationAsync(
            innerCt => DeleteCoreAsync(
                portfolioId,
                _db.CapitalAssets.Where(asset => asset.PortfolioId == portfolioId),
                id,
                innerCt),
            ct);
        await BroadcastDeletionAsync(portfolioId, id, result, ct);
        return result.Deleted;
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var result = await _db.ExecuteAuthorizedMutationAsync(
            innerCt => DeleteCoreAsync(
                scope.PortfolioId,
                AuthorizedAssets(scope, WriteCapabilities, tracking: true),
                id,
                innerCt),
            ct);
        await BroadcastDeletionAsync(scope.PortfolioId, id, result, ct);
        return result.Deleted;
    }

    private async Task<CapitalAssetDeleteResult> DeleteCoreAsync(
        int portfolioId,
        IQueryable<CapitalAsset> assets,
        int id,
        CancellationToken ct)
    {
        var entity = await assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (entity is null)
            return new CapitalAssetDeleteResult(false, []);

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;

        var linkedExpenseQuery = _db.Expenses
            .Where(e => e.PortfolioId == portfolioId && e.CapitalizedAssetId == id);
        var linkedExpenseIds = await linkedExpenseQuery
            .Select(expense => expense.Id)
            .ToArrayAsync(ct);
        await linkedExpenseQuery.ExecuteUpdateAsync(setters => setters
            .SetProperty(expense => expense.CapitalizedAssetId, (int?)null)
            .SetProperty(expense => expense.UpdatedAt, now), ct);

        await _db.SaveChangesAsync(ct);

        return new CapitalAssetDeleteResult(true, linkedExpenseIds);
    }

    private async Task BroadcastDeletionAsync(
        int portfolioId,
        int id,
        CapitalAssetDeleteResult result,
        CancellationToken ct)
    {
        if (!result.Deleted)
            return;
        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        var linkedExpenses = await _db.Expenses.AsNoTracking()
            .Where(expense => result.LinkedExpenseIds.Contains(expense.Id))
            .ToListAsync(ct);
        foreach (var expense in linkedExpenses)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, ExpenseEntityType, expense.Id, ExpenseResponse.FromEntity(expense), ct);
    }

    private sealed record CapitalAssetDeleteResult(bool Deleted, int[] LinkedExpenseIds);

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

    private static IQueryable<CapitalAsset> BuildListQuery(
        IQueryable<CapitalAsset> assets,
        CapitalAssetListQuery query)
    {
        var q = assets;

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

    private IQueryable<CapitalAsset> AuthorizedAssets(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities,
        bool tracking = false)
    {
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());
        var assets = tracking ? _db.CapitalAssets : _db.CapitalAssets.AsNoTracking();
        return assets.Where(asset =>
            asset.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == asset.PropertyId &&
                property.PortfolioId == asset.PortfolioId));
    }
}
