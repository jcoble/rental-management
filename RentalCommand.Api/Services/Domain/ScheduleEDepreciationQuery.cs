using Microsoft.EntityFrameworkCore;
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
        int? propertyId = null)
    {
        var yearEndExclusive = new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var properties = db.Properties.AsNoTracking().Where(property => property.PortfolioId == portfolioId);
        var assets = db.CapitalAssets.AsNoTracking().Where(asset =>
            asset.PortfolioId == portfolioId &&
            asset.InServiceDate < yearEndExclusive &&
            asset.DisposedOnDate == null);
        if (propertyId.HasValue)
        {
            properties = properties.Where(property => property.Id == propertyId.Value);
            assets = assets.Where(asset => asset.PropertyId == propertyId.Value);
        }

        var propertyComponents =
            from property in properties
            let buildingBasis = property.PurchasePrice.HasValue
                ? Math.Round(property.PurchasePrice.Value - (property.LandValue ?? 0m), 2)
                : (decimal?)null
            let remaining = buildingBasis.HasValue
                ? (buildingBasis.Value - property.AccumulatedDepreciation > 0m
                    ? buildingBasis.Value - property.AccumulatedDepreciation
                    : 0m)
                : (decimal?)null
            let computedAnnual = buildingBasis.HasValue && buildingBasis.Value > 0m &&
                                 property.InServiceDate.HasValue && year >= property.InServiceDate.Value.Year
                ? Math.Round(
                    buildingBasis.Value / 27.5m *
                    (year == property.InServiceDate.Value.Year
                        ? 12 - property.InServiceDate.Value.Month + 0.5m
                        : 12m) / 12m,
                    2)
                : 0m
            let requestedAmount = property.ManualAnnualDepreciation.HasValue
                ? Math.Round(property.ManualAnnualDepreciation.Value, 2)
                : computedAnnual
            let nonNegativeAmount = requestedAmount > 0m ? requestedAmount : 0m
            let cappedAmount = remaining.HasValue && nonNegativeAmount > remaining.Value
                ? remaining.Value
                : nonNegativeAmount
            let finalAmount = cappedAmount
            where finalAmount > 0m
            select new ScheduleEDepreciationComponent
            {
                PropertyId = property.Id,
                Amount = finalAmount,
                FirstYearEstimateMarker = !property.ManualAnnualDepreciation.HasValue &&
                                          property.InServiceDate.HasValue &&
                                          year == property.InServiceDate.Value.Year ? 1 : 0,
            };

        var assetComponents =
            from asset in assets
            let basis = Math.Round(asset.CostBasis, 2)
            let remaining = basis - asset.AccumulatedDepreciation > 0m
                ? basis - asset.AccumulatedDepreciation
                : 0m
            let yearIndex = year - asset.InServiceDate.Year
            let straightLineAnnual = asset.RecoveryYears > 0m ? basis / asset.RecoveryYears : 0m
            let straightLineAmount = asset.Method == DepreciationMethod.StraightLine &&
                                     asset.Convention == DepreciationConvention.MidMonth && yearIndex >= 0
                ? Math.Round(straightLineAnnual *
                    (yearIndex == 0 ? 12 - asset.InServiceDate.Month + 0.5m : 12m) / 12m, 2)
                : asset.Method == DepreciationMethod.StraightLine &&
                  asset.Convention == DepreciationConvention.HalfYear &&
                  yearIndex >= 0 && yearIndex <= asset.RecoveryYears
                    ? Math.Round(straightLineAnnual *
                        (yearIndex == 0 || yearIndex == asset.RecoveryYears ? 0.5m : 1m), 2)
                    : 0m
            let macrsRate = asset.Method == DepreciationMethod.Macrs &&
                            asset.Convention == DepreciationConvention.HalfYear
                ? asset.RecoveryYears == 5m
                    ? yearIndex == 0 ? 0.20m : yearIndex == 1 ? 0.32m : yearIndex == 2 ? 0.192m :
                      yearIndex == 3 ? 0.1152m : yearIndex == 4 ? 0.1152m : yearIndex == 5 ? 0.0576m : 0m
                    : asset.RecoveryYears == 7m
                        ? yearIndex == 0 ? 0.1429m : yearIndex == 1 ? 0.2449m : yearIndex == 2 ? 0.1749m :
                          yearIndex == 3 ? 0.1249m : yearIndex == 4 ? 0.0893m : yearIndex == 5 ? 0.0892m :
                          yearIndex == 6 ? 0.0893m : yearIndex == 7 ? 0.0446m : 0m
                        : asset.RecoveryYears == 15m
                            ? yearIndex == 0 ? 0.05m : yearIndex == 1 ? 0.095m : yearIndex == 2 ? 0.0855m :
                              yearIndex == 3 ? 0.077m : yearIndex == 4 ? 0.0693m : yearIndex == 5 ? 0.0623m :
                              yearIndex == 6 ? 0.059m : yearIndex == 7 ? 0.059m : yearIndex == 8 ? 0.0591m :
                              yearIndex == 9 ? 0.059m : yearIndex == 10 ? 0.0591m : yearIndex == 11 ? 0.059m :
                              yearIndex == 12 ? 0.0591m : yearIndex == 13 ? 0.059m : yearIndex == 14 ? 0.0591m :
                              yearIndex == 15 ? 0.0295m : 0m
                            : 0m
                : 0m
            let requestedAmount = asset.Method == DepreciationMethod.Macrs
                ? Math.Round(basis * macrsRate, 2)
                : straightLineAmount
            let cappedAmount = requestedAmount > remaining ? remaining : requestedAmount
            where basis > 0m && remaining > 0m && yearIndex >= 0 && cappedAmount > 0m
            select new ScheduleEDepreciationComponent
            {
                PropertyId = asset.PropertyId,
                Amount = cappedAmount,
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
