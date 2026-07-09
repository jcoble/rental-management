using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Services;

public readonly record struct PropertyDispositionTaxResult(
    decimal PurchasePrice,
    decimal LandValue,
    decimal BuildingBasis,
    decimal AccumulatedDepreciationBeforeSale,
    decimal SaleYearDepreciation,
    decimal TotalDepreciation,
    decimal AdjustedBasis,
    decimal NetSaleProceeds,
    decimal GainLoss,
    decimal UnrecapturedSection1250Gain);

public static class PropertyDispositionCalculator
{
    public static PropertyDispositionTaxResult Calculate(
        PropertyDepreciationBasis basis,
        DateTime closedOnDate,
        decimal salePrice,
        decimal sellingCosts)
    {
        var purchasePrice = Round(basis.PurchasePrice ?? 0m);
        var landValue = Round(basis.LandValue ?? 0m);
        var buildingBasis = Math.Max(0m, Round(purchasePrice - landValue));
        var accumulatedBeforeSale = Math.Min(buildingBasis, Math.Max(0m, Round(basis.AccumulatedDepreciation)));
        var saleYearDepreciation = CalculateSaleYearDepreciation(basis, closedOnDate, buildingBasis, accumulatedBeforeSale);
        var totalDepreciation = Math.Min(buildingBasis, Round(accumulatedBeforeSale + saleYearDepreciation));
        var adjustedBasis = Round(purchasePrice - totalDepreciation);
        var netSaleProceeds = Round(salePrice - sellingCosts);
        var gainLoss = Round(netSaleProceeds - adjustedBasis);
        var unrecapturedSection1250Gain = DepreciationCalculator.UnrecapturedSec1250Gain(gainLoss, totalDepreciation);

        return new PropertyDispositionTaxResult(
            purchasePrice,
            landValue,
            buildingBasis,
            accumulatedBeforeSale,
            saleYearDepreciation,
            totalDepreciation,
            adjustedBasis,
            netSaleProceeds,
            gainLoss,
            unrecapturedSection1250Gain);
    }

    private static decimal CalculateSaleYearDepreciation(
        PropertyDepreciationBasis basis,
        DateTime closedOnDate,
        decimal buildingBasis,
        decimal accumulatedBeforeSale)
    {
        if (buildingBasis <= 0m || basis.InServiceDate is not { } inService || closedOnDate.Year < inService.Year)
            return 0m;

        var remaining = Math.Max(0m, buildingBasis - accumulatedBeforeSale);
        if (remaining <= 0m)
            return 0m;

        var monthsHeld = MidMonthMonthsHeldInDispositionYear(inService, closedOnDate);
        if (monthsHeld <= 0m)
            return 0m;

        var annual = basis.ManualAnnualDepreciation is { } manual
            ? Round(manual)
            : Round(buildingBasis / RecoveryClass.ResidentialBuilding);
        var prorated = Round(annual * monthsHeld / 12m);
        return Math.Min(prorated, remaining);
    }

    private static decimal MidMonthMonthsHeldInDispositionYear(DateTime inService, DateTime closedOnDate)
    {
        if (closedOnDate.Year == inService.Year)
            return Math.Max(0m, closedOnDate.Month - inService.Month);

        return closedOnDate.Month - 0.5m;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
