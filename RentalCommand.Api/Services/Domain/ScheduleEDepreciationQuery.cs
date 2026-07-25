using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Shared SQL-side Schedule E depreciation projection. Property and capital-asset components,
/// annual-basis calculations, filtering, grouping, and portfolio totals remain one composable query.
/// </summary>
internal static class ScheduleEDepreciationQuery
{
    internal static IQueryable<ScheduleEDepreciationRow> Build(
        RentalCommandDbContext db,
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties,
        int? propertyId = null)
    {
        var yearEndExclusive = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var properties = authorizedProperties.Where(property => property.PortfolioId == portfolioId);
        var assets = db.CapitalAssets.AsNoTracking().Where(asset =>
            asset.PortfolioId == portfolioId &&
            authorizedProperties.Any(property => property.Id == asset.PropertyId) &&
            asset.InServiceDate < yearEndExclusive &&
            asset.DisposedOnDate == null);
        if (propertyId.HasValue)
        {
            properties = properties.Where(property => property.Id == propertyId.Value);
            assets = assets.Where(asset => asset.PropertyId == propertyId.Value);
        }

        var propertyComponents =
            from property in properties
            let amount = ScheduleEDepreciationDbFunction.AnnualAmount(
                property.PurchasePrice,
                property.LandValue,
                property.InServiceDate,
                property.ManualAnnualDepreciation,
                (int)DepreciationMethod.StraightLine,
                27.5m,
                (int)DepreciationConvention.MidMonth,
                property.AccumulatedDepreciation,
                year)
            where amount > 0m
            select new ScheduleEDepreciationComponent
            {
                PropertyId = property.Id,
                Amount = amount,
                FirstYearEstimateMarker = !property.ManualAnnualDepreciation.HasValue &&
                                          property.InServiceDate.HasValue &&
                                          year == property.InServiceDate.Value.Year ? 1 : 0,
            };

        var assetComponents =
            from asset in assets
            let yearIndex = year - asset.InServiceDate.Year
            let amount = ScheduleEDepreciationDbFunction.AnnualAmount(
                asset.CostBasis,
                null,
                asset.InServiceDate,
                null,
                (int)asset.Method,
                asset.RecoveryYears,
                (int)asset.Convention,
                asset.AccumulatedDepreciation,
                year)
            where yearIndex >= 0 && amount > 0m
            select new ScheduleEDepreciationComponent
            {
                PropertyId = asset.PropertyId,
                Amount = amount,
                FirstYearEstimateMarker = yearIndex == 0 ? 1 : 0,
            };

        var components = propertyComponents.Concat(assetComponents);
        return components
            .GroupBy(component => component.PropertyId)
            .Select(group => new ScheduleEDepreciationRow
            {
                PropertyId = group.Key,
                Amount = group.Sum(component => component.Amount),
                IsFirstYearEstimate = group.Max(component => component.FirstYearEstimateMarker) == 1,
                TotalAmount = components.Sum(component => (decimal?)component.Amount) ?? 0m,
            });
    }

    private sealed class ScheduleEDepreciationComponent
    {
        public int PropertyId { get; set; }
        public decimal Amount { get; set; }
        public int FirstYearEstimateMarker { get; set; }
    }
}

internal sealed class ScheduleEDepreciationRow
{
    public int PropertyId { get; set; }
    public decimal Amount { get; set; }
    public bool IsFirstYearEstimate { get; set; }
    public decimal TotalAmount { get; set; }
}
