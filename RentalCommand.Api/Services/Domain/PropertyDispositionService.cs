using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public class PropertyDispositionService : IPropertyDispositionService
{
    private const string EntityType = "PropertyDisposition";
    private static readonly string[] ReadCapabilities = [CapabilityKeys.MoneyOwnerReportsRead];
    private static readonly string[] WriteCapabilities = [CapabilityKeys.RentalsManage];

    private readonly RentalCommandDbContext _db;
    private static readonly AtomicJsonResultCodec<CreatePropertyDispositionResult> CreateCodec =
        new("property-disposition.create.v1");
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;

    public PropertyDispositionService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        ICurrentActor actor,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _atomic = atomic;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _ = actor;
    }

    public async Task<IReadOnlyList<PropertyDispositionResponse>> ListAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<PropertyDispositionListResponse> ListPageAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default)
        => await ListPageFromQueryAsync(
            _db.PropertyDispositions.AsNoTracking().Where(item => item.PortfolioId == portfolioId),
            query,
            ct);

    public async Task<IReadOnlyList<PropertyDispositionResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, PropertyDispositionListQuery query, CancellationToken ct = default)
        => (await ListPageAuthorizedAsync(scope, query, ct)).Items;

    public Task<PropertyDispositionListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, PropertyDispositionListQuery query, CancellationToken ct = default)
        => ListPageFromQueryAsync(AuthorizedDispositions(scope, ReadCapabilities), query, ct);

    private async Task<PropertyDispositionListResponse> ListPageFromQueryAsync(
        IQueryable<PropertyDisposition> dispositions,
        PropertyDispositionListQuery query,
        CancellationToken ct)
    {
        var filtered = BuildListQuery(dispositions, query);
        var totalCount = await filtered.CountAsync(ct);
        var rows = await ProjectRows(ApplySort(filtered, query))
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new PropertyDispositionListResponse
        {
            Items = rows.Select(ToResponse).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<PropertyDispositionResponse?> GetAsync(
        int portfolioId, int id, CancellationToken ct = default)
    {
        var row = await ProjectRows(_db.PropertyDispositions
                .AsNoTracking()
                .Where(d => d.Id == id && d.PortfolioId == portfolioId))
            .FirstOrDefaultAsync(ct);

        return row is null ? null : ToResponse(row);
    }

    public async Task<PropertyDispositionResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        var row = await ProjectRows(
                AuthorizedDispositions(scope, ReadCapabilities).Where(item => item.Id == id))
            .FirstOrDefaultAsync(ct);
        return row == null ? null : ToResponse(row);
    }

    public async Task<PropertyDispositionResponse?> CreateAsync(
        ActiveAccessContext accessContext, CreatePropertyDispositionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var atomic = _atomic
            ?? throw new InvalidOperationException("Atomic property disposition is not configured.");
        var portfolioId = accessContext.PortfolioId;
        var closedOn = request.ClosedOnDate.ToUtc().Date;
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey.Trim())))
            .ToLowerInvariant();
        var deliveryKey = $"property-disposition:{portfolioId}:{request.PropertyId}:{digest}";
        var outcome = await atomic.ExecuteAsync(
            new AtomicCommandIdentity("property-disposition.create", deliveryKey),
            new CreatePropertyDispositionCommand(portfolioId, request.PropertyId, closedOn,
                request.SalePrice, request.SellingCosts, Normalize(request.BuyerName),
                Normalize(request.Memo), accessContext.UserId, accessContext.SessionId,
                accessContext.AccessContextId, accessContext.AccessRevision, deliveryKey),
            CreateCodec, ct);
        if (outcome.Value.Outcome == CreatePropertyDispositionOutcome.PropertyNotFoundOrAlreadyDisposed
            || outcome.Value.DispositionId is not { } dispositionId)
            return null;

        return await GetAsync(portfolioId, dispositionId, ct)
            ?? throw new InvalidOperationException("Committed property disposition could not be read back.");
    }

    public async Task<PropertyDispositionResponse?> UpdateAsync(
        int portfolioId, int id, UpdatePropertyDispositionRequest request, CancellationToken ct = default)
        => await UpdateCoreAsync(
            portfolioId,
            _db.PropertyDispositions.Where(item => item.PortfolioId == portfolioId),
            id,
            request,
            broadcast: true,
            ct);

    public async Task<PropertyDispositionResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdatePropertyDispositionRequest request,
        CancellationToken ct = default)
    {
        var response = await _db.ExecuteAuthorizedMutationAsync(
            innerCt => UpdateCoreAsync(
                scope.PortfolioId,
                AuthorizedDispositions(scope, WriteCapabilities, tracking: true),
                id,
                request,
                broadcast: false,
                innerCt),
            ct);
        if (response != null)
            await _dataUpdate.BroadcastEntityUpdateAsync(scope.PortfolioId, EntityType, id, response, ct);
        return response;
    }

    private async Task<PropertyDispositionResponse?> UpdateCoreAsync(
        int portfolioId,
        IQueryable<PropertyDisposition> dispositions,
        int id,
        UpdatePropertyDispositionRequest request,
        bool broadcast,
        CancellationToken ct)
    {
        var entity = await dispositions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null)
            return null;

        if (request.ClosedOnDate.HasValue) entity.ClosedOnDate = request.ClosedOnDate.Value.ToUtc().Date;
        if (request.SalePrice.HasValue) entity.SalePrice = request.SalePrice.Value;
        if (request.SellingCosts.HasValue) entity.SellingCosts = request.SellingCosts.Value;
        if (request.BuyerName != null) entity.BuyerName = Normalize(request.BuyerName);
        if (request.Memo != null) entity.Memo = Normalize(request.Memo);
        entity.UpdatedAt = _timeProvider.UtcNow();

        await _db.SaveChangesAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? PropertyDispositionResponse.FromEntity(entity);
        if (broadcast)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
        => await DeleteCoreAsync(
            portfolioId,
            _db.PropertyDispositions.Where(item => item.PortfolioId == portfolioId),
            id,
            broadcast: true,
            ct);

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var deleted = await _db.ExecuteAuthorizedMutationAsync(
            innerCt => DeleteCoreAsync(
                scope.PortfolioId,
                AuthorizedDispositions(scope, WriteCapabilities, tracking: true),
                id,
                broadcast: false,
                innerCt),
            ct);
        if (deleted)
            await _dataUpdate.BroadcastEntityDeleteAsync(scope.PortfolioId, EntityType, id, ct);
        return deleted;
    }

    private async Task<bool> DeleteCoreAsync(
        int portfolioId,
        IQueryable<PropertyDisposition> dispositions,
        int id,
        bool broadcast,
        CancellationToken ct)
    {
        var entity = await dispositions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null)
            return false;

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        if (broadcast)
            await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private static IQueryable<PropertyDisposition> BuildListQuery(
        IQueryable<PropertyDisposition> dispositions,
        PropertyDispositionListQuery query)
    {
        var q = dispositions;

        if (query.PropertyId.HasValue)
            q = q.Where(d => d.PropertyId == query.PropertyId.Value);

        if (query.Year.HasValue)
        {
            var start = new DateTime(query.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var end = start.AddYears(1);
            q = q.Where(d => d.ClosedOnDate >= start && d.ClosedOnDate < end);
        }

        var (from, to) = ListDateRange.UtcDay(query.From, query.To);
        if (from is { } fromUtc)
            q = q.Where(d => d.ClosedOnDate >= fromUtc);
        if (to is { } toUtcExclusive)
            q = q.Where(d => d.ClosedOnDate < toUtcExclusive);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d =>
                EF.Functions.ILike(d.Property!.Name, $"%{term}%") ||
                (d.BuyerName != null && EF.Functions.ILike(d.BuyerName, $"%{term}%")) ||
                (d.Memo != null && EF.Functions.ILike(d.Memo, $"%{term}%")));
        }

        return q;
    }

    private static IQueryable<PropertyDisposition> ApplySort(IQueryable<PropertyDisposition> q, ListQuery query)
    {
        var ordered = query.SortField switch
        {
            "property" or "propertyname" => query.SortDescending ? q.OrderByDescending(d => d.Property!.Name) : q.OrderBy(d => d.Property!.Name),
            "saleprice" => query.SortDescending ? q.OrderByDescending(d => d.SalePrice) : q.OrderBy(d => d.SalePrice),
            "sellingcosts" => query.SortDescending ? q.OrderByDescending(d => d.SellingCosts) : q.OrderBy(d => d.SellingCosts),
            "buyer" or "buyername" => query.SortDescending ? q.OrderByDescending(d => d.BuyerName) : q.OrderBy(d => d.BuyerName),
            "updatedat" => query.SortDescending ? q.OrderByDescending(d => d.UpdatedAt) : q.OrderBy(d => d.UpdatedAt),
            "createdat" => query.SortDescending ? q.OrderByDescending(d => d.CreatedAt) : q.OrderBy(d => d.CreatedAt),
            "closedondate" or "date" => query.SortDescending ? q.OrderByDescending(d => d.ClosedOnDate) : q.OrderBy(d => d.ClosedOnDate),
            _ => q.OrderByDescending(d => d.ClosedOnDate),
        };

        return ordered.ThenByDescending(d => d.Id);
    }

    private static IQueryable<DispositionRow> ProjectRows(IQueryable<PropertyDisposition> query)
    {
        return query.Select(d => new DispositionRow
        {
            Id = d.Id,
            PortfolioId = d.PortfolioId,
            PropertyId = d.PropertyId,
            PropertyName = d.Property!.Name,
            ClosedOnDate = d.ClosedOnDate,
            SalePrice = d.SalePrice,
            SellingCosts = d.SellingCosts,
            BuyerName = d.BuyerName,
            Memo = d.Memo,
            PurchasePrice = d.Property.PurchasePrice,
            LandValue = d.Property.LandValue,
            InServiceDate = d.Property.InServiceDate,
            ManualAnnualDepreciation = d.Property.ManualAnnualDepreciation,
            AccumulatedDepreciation = d.Property.AccumulatedDepreciation,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        });
    }

    private static PropertyDispositionResponse ToResponse(DispositionRow row)
    {
        var tax = PropertyDispositionCalculator.Calculate(
            new PropertyDepreciationBasis(
                row.PurchasePrice,
                row.LandValue,
                row.InServiceDate,
                row.ManualAnnualDepreciation,
                row.AccumulatedDepreciation),
            row.ClosedOnDate,
            row.SalePrice,
            row.SellingCosts);

        return new PropertyDispositionResponse
        {
            Id = row.Id,
            PortfolioId = row.PortfolioId,
            PropertyId = row.PropertyId,
            PropertyName = row.PropertyName,
            ClosedOnDate = row.ClosedOnDate,
            SalePrice = row.SalePrice,
            SellingCosts = row.SellingCosts,
            NetSaleProceeds = tax.NetSaleProceeds,
            PurchasePrice = tax.PurchasePrice,
            LandValue = tax.LandValue,
            BuildingBasis = tax.BuildingBasis,
            AccumulatedDepreciationBeforeSale = tax.AccumulatedDepreciationBeforeSale,
            SaleYearDepreciation = tax.SaleYearDepreciation,
            TotalDepreciation = tax.TotalDepreciation,
            AdjustedBasis = tax.AdjustedBasis,
            GainLoss = tax.GainLoss,
            UnrecapturedSection1250Gain = tax.UnrecapturedSection1250Gain,
            BuyerName = row.BuyerName,
            Memo = row.Memo,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private IQueryable<PropertyDisposition> AuthorizedDispositions(
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilities,
        bool tracking = false)
    {
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());
        var dispositions = tracking
            ? _db.PropertyDispositions
            : _db.PropertyDispositions.AsNoTracking();
        return dispositions.Where(disposition =>
            disposition.PortfolioId == scope.PortfolioId &&
            authorizedProperties.Any(property =>
                property.Id == disposition.PropertyId &&
                property.PortfolioId == disposition.PortfolioId));
    }

    private sealed class DispositionRow
    {
        public int Id { get; init; }
        public int PortfolioId { get; init; }
        public int PropertyId { get; init; }
        public string? PropertyName { get; init; }
        public DateTime ClosedOnDate { get; init; }
        public decimal SalePrice { get; init; }
        public decimal SellingCosts { get; init; }
        public string? BuyerName { get; init; }
        public string? Memo { get; init; }
        public decimal? PurchasePrice { get; init; }
        public decimal? LandValue { get; init; }
        public DateTime? InServiceDate { get; init; }
        public decimal? ManualAnnualDepreciation { get; init; }
        public decimal AccumulatedDepreciation { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
