using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Money;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ICapitalAssetService"/>
public class CapitalAssetService : ICapitalAssetService
{
    private static readonly string[] ReadCapabilities =
        [CapabilityKeys.ReportsRead, CapabilityKeys.MoneyOwnerReportsRead, CapabilityKeys.MoneyExpensesManage];

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public CapitalAssetService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
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

    public async Task<CapitalAssetResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope,
        CreateCapitalAssetRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Create, 0, operationKey, request,
            _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found
            ? await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct: ct)
            : null;
    }

    public async Task<CapitalAssetResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateCapitalAssetRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Update, id, operationKey, request,
            _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found
            ? await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct: ct)
            : null;
    }

    public async Task<CapitalAssetResponse?> CapitalizeExpenseAuthorizedAsync(
        WorkspaceReadScope scope,
        int expenseId,
        CapitalizeExpenseRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.CapitalizeExpense,
            expenseId, operationKey, request, _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found
            ? await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct: ct)
            : null;
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Delete, id, operationKey, new object(),
            _timeProvider.UtcNow());
        var outcome = await _atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found;
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

    private IQueryable<CapitalAsset> AuthorizedAssets(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities)
    {
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());
        return _db.CapitalAssets.AsNoTracking().Where(asset =>
            asset.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == asset.PropertyId &&
                property.PortfolioId == asset.PortfolioId));
    }
}
