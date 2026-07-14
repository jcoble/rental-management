using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public class PropertyDispositionService : IPropertyDispositionService
{
    private static readonly string[] ReadCapabilities = [CapabilityKeys.MoneyOwnerReportsRead];

    private readonly RentalCommandDbContext _db;
    private static readonly AtomicJsonResultCodec<CreatePropertyDispositionResult> CreateCodec =
        new("property-disposition.create.v1");
    private readonly IAtomicUnitOfWork? _atomic;
    private readonly TimeProvider _timeProvider;

    public PropertyDispositionService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAtomicUnitOfWork? atomic = null)
    {
        _db = db;
        _atomic = atomic;
        _timeProvider = timeProvider;
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

    public async Task<PropertyDispositionResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdatePropertyDispositionRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var atomic = _atomic
            ?? throw new InvalidOperationException("Atomic property disposition is not configured.");
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.RentalsManage,
            AtomicMoneyDomain.PropertyDisposition, AtomicMoneyOperation.Update, id, operationKey, request);
        var outcome = await atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found
            ? await GetAsync(scope.PortfolioId, outcome.Value.EntityId, ct)
            : null;
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var atomic = _atomic
            ?? throw new InvalidOperationException("Atomic property disposition is not configured.");
        var command = AtomicMoneyMutation.Command(scope, CapabilityKeys.RentalsManage,
            AtomicMoneyDomain.PropertyDisposition, AtomicMoneyOperation.Delete, id, operationKey, new object());
        var outcome = await atomic.ExecuteAsync(
            AtomicMoneyMutation.Identity(command), command, AtomicMoneyMutation.Codec, ct);
        return outcome.Value.Found;
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
        IReadOnlyCollection<string> capabilities)
    {
        var authorizedProperties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, _timeProvider.UtcNow());
        return _db.PropertyDispositions.AsNoTracking().Where(disposition =>
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
