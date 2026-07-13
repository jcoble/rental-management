using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class PropertyDispositionService : IPropertyDispositionService
{
    private const string EntityType = "PropertyDisposition";
    private const string PropertyEntityType = "Property";
    private const string LeaseEntityType = "Lease";
    private const string UnitEntityType = "Unit";
    private const string CapitalAssetEntityType = "CapitalAsset";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly ICurrentActor _actor;

    public PropertyDispositionService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        ICurrentActor actor)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _actor = actor;
    }

    public async Task<IReadOnlyList<PropertyDispositionResponse>> ListAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, query, ct);
        return page.Items;
    }

    public async Task<PropertyDispositionListResponse> ListPageAsync(
        int portfolioId, PropertyDispositionListQuery query, CancellationToken ct = default)
    {
        var filtered = BuildListQuery(portfolioId, query);
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

    public async Task<PropertyDispositionResponse?> CreateAsync(
        int portfolioId, CreatePropertyDispositionRequest request, CancellationToken ct = default)
    {
        var property = await _db.Properties
            .FirstOrDefaultAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct);
        if (property is null)
            return null;

        var alreadyDisposed = await _db.PropertyDispositions
            .AnyAsync(d => d.PortfolioId == portfolioId && d.PropertyId == request.PropertyId, ct);
        if (alreadyDisposed)
            return null;

        var now = _timeProvider.UtcNow();
        var closedOn = request.ClosedOnDate.ToUtc().Date;
        var entity = new PropertyDisposition
        {
            PortfolioId = portfolioId,
            PropertyId = request.PropertyId,
            ClosedOnDate = closedOn,
            SalePrice = request.SalePrice,
            SellingCosts = request.SellingCosts,
            BuyerName = Normalize(request.BuyerName),
            Memo = Normalize(request.Memo),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.PropertyDispositions.Add(entity);
        property.Status = PropertyStatus.Inactive;
        property.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        await _db.Leases
            .Where(l => l.PortfolioId == portfolioId &&
                        l.PropertyId == property.Id &&
                        (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(lease => lease.Status, LeaseStatus.Terminated)
                .SetProperty(lease => lease.EndDate, lease => lease.EndDate > closedOn ? closedOn : lease.EndDate)
                .SetProperty(lease => lease.MoveOutDate, closedOn)
                .SetProperty(lease => lease.UpdatedAt, now), ct);

        var actorUserId = _actor.UserId
            ?? throw new InvalidOperationException("Property disposition requires an authenticated actor.");
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UnitOperationalPeriods"
                ("PortfolioId", "PropertyId", "UnitId", "Type", "StartedAtUtc",
                 "Reason", "CreatedAtUtc", "CreatedByUserId")
            SELECT unit."PortfolioId", unit."PropertyId", unit."Id", 'ManagementHold', {now},
                   'Property disposed; unit is no longer operational.', {now}, {actorUserId}
            FROM "Units" AS unit
            WHERE unit."PortfolioId" = {portfolioId}
              AND unit."PropertyId" = {property.Id}
              AND unit."DeletedAt" IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM "UnitOperationalPeriods" AS period
                WHERE period."PortfolioId" = unit."PortfolioId"
                  AND period."UnitId" = unit."Id"
                  AND period."Type" = 'ManagementHold'
                  AND period."EndedAtUtc" IS NULL)
            """, ct);

        await _db.Units
            .Where(unit => unit.PortfolioId == portfolioId && unit.PropertyId == property.Id)
            .ExecuteUpdateAsync(updates => updates.SetProperty(unit => unit.UpdatedAt, now), ct);

        await _db.CapitalAssets
            .Where(a => a.PortfolioId == portfolioId &&
                        a.PropertyId == property.Id &&
                        a.DisposedOnDate == null)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(asset => asset.DisposedOnDate, closedOn)
                .SetProperty(asset => asset.UpdatedAt, now), ct);

        var leases = await _db.Leases.AsNoTracking()
            .Where(lease => lease.PortfolioId == portfolioId
                && lease.PropertyId == property.Id
                && lease.Status == LeaseStatus.Terminated
                && lease.MoveOutDate == closedOn)
            .ToListAsync(ct);
        var units = await BuildDerivedUnitResponses(portfolioId, property.Id).ToListAsync(ct);
        var capitalAssets = await _db.CapitalAssets.AsNoTracking()
            .Where(asset => asset.PortfolioId == portfolioId
                && asset.PropertyId == property.Id
                && asset.DisposedOnDate == closedOn)
            .ToListAsync(ct);

        await tx.CommitAsync(ct);

        var response = await GetAsync(portfolioId, entity.Id, ct) ?? PropertyDispositionResponse.FromEntity(entity);
        await BroadcastRelatedUpdatesAsync(portfolioId, property, leases, units, capitalAssets, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<PropertyDispositionResponse?> UpdateAsync(
        int portfolioId, int id, UpdatePropertyDispositionRequest request, CancellationToken ct = default)
    {
        var entity = await _db.PropertyDispositions
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);
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
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.PropertyDispositions
            .FirstOrDefaultAsync(d => d.Id == id && d.PortfolioId == portfolioId, ct);
        if (entity is null)
            return false;

        var now = _timeProvider.UtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _dataUpdate.BroadcastEntityDeleteAsync(portfolioId, EntityType, id, ct);
        return true;
    }

    private IQueryable<PropertyDisposition> BuildListQuery(int portfolioId, PropertyDispositionListQuery query)
    {
        var q = _db.PropertyDispositions
            .AsNoTracking()
            .Where(d => d.PortfolioId == portfolioId);

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

    private async Task BroadcastRelatedUpdatesAsync(
        int portfolioId,
        Property property,
        IReadOnlyCollection<Lease> leases,
        IReadOnlyCollection<UnitResponse> units,
        IReadOnlyCollection<CapitalAsset> capitalAssets,
        CancellationToken ct)
    {
        var unitCount = await _db.Units.CountAsync(u => u.PropertyId == property.Id, ct);
        var occupiedUnits = await _db.UnitOccupancyProjections.CountAsync(
            row => row.PortfolioId == portfolioId
                && row.PropertyId == property.Id
                && row.IsOccupied, ct);
        await _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId,
            PropertyEntityType,
            property.Id,
            PropertyResponse.FromEntity(property, unitCount, occupiedUnits),
            ct);

        foreach (var lease in leases)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, LeaseEntityType, lease.Id, LeaseResponse.FromEntity(lease), ct);

        foreach (var unit in units)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, UnitEntityType, unit.Id, unit, ct);

        foreach (var asset in capitalAssets)
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, CapitalAssetEntityType, asset.Id, CapitalAssetResponse.FromEntity(asset, property.UpdatedAt.Year), ct);
    }

    private IQueryable<UnitResponse> BuildDerivedUnitResponses(int portfolioId, int propertyId) =>
        from unit in _db.Units.AsNoTracking()
        where unit.PortfolioId == portfolioId && unit.PropertyId == propertyId
        join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            on new { unit.PortfolioId, UnitId = unit.Id }
            equals new { occupancy.PortfolioId, occupancy.UnitId }
        select new UnitResponse
        {
            Id = unit.Id,
            PropertyId = unit.PropertyId,
            UnitNumber = unit.UnitNumber,
            FloorPlan = unit.FloorPlan,
            Bedrooms = unit.Bedrooms,
            Bathrooms = unit.Bathrooms,
            SquareFeet = unit.SquareFeet,
            MarketRent = unit.MarketRent,
            Status = occupancy.IsInTurnover || occupancy.IsOutOfService || occupancy.IsOnManagementHold
                ? DerivedUnitStatus.Offline
                : occupancy.IsOccupied
                    ? DerivedUnitStatus.Occupied
                    : occupancy.HasScheduledMoveIn
                        ? DerivedUnitStatus.Reserved
                        : DerivedUnitStatus.Vacant,
            Notes = unit.Notes,
            CreatedAt = unit.CreatedAt,
            UpdatedAt = unit.UpdatedAt,
        };

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
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
