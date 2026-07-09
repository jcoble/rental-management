using FluentAssertions;
using RentalCommand.Core.Services;

namespace RentalCommand.Core.Tests;

public class PropertyDispositionCalculatorTests
{
    [Fact]
    public void Calculate_IncludesSaleYearDepreciationAndGainLoss()
    {
        var basis = new PropertyDepreciationBasis(
            PurchasePrice: 300_000m,
            LandValue: 60_000m,
            InServiceDate: new DateTime(2020, 1, 1),
            ManualAnnualDepreciation: null,
            AccumulatedDepreciation: 80_000m);

        var result = PropertyDispositionCalculator.Calculate(
            basis,
            closedOnDate: new DateTime(2026, 7, 15),
            salePrice: 350_000m,
            sellingCosts: 10_000m);

        result.BuildingBasis.Should().Be(240_000m);
        result.SaleYearDepreciation.Should().Be(4_727.27m);
        result.TotalDepreciation.Should().Be(84_727.27m);
        result.AdjustedBasis.Should().Be(215_272.73m);
        result.NetSaleProceeds.Should().Be(340_000m);
        result.GainLoss.Should().Be(124_727.27m);
        result.UnrecapturedSection1250Gain.Should().Be(84_727.27m);
    }

    [Fact]
    public void Calculate_ClampsDepreciationAtBuildingBasis()
    {
        var basis = new PropertyDepreciationBasis(
            PurchasePrice: 300_000m,
            LandValue: 60_000m,
            InServiceDate: new DateTime(1990, 1, 1),
            ManualAnnualDepreciation: null,
            AccumulatedDepreciation: 239_000m);

        var result = PropertyDispositionCalculator.Calculate(
            basis,
            closedOnDate: new DateTime(2026, 12, 1),
            salePrice: 280_000m,
            sellingCosts: 5_000m);

        result.SaleYearDepreciation.Should().Be(1_000m);
        result.TotalDepreciation.Should().Be(240_000m);
        result.AdjustedBasis.Should().Be(60_000m);
        result.GainLoss.Should().Be(215_000m);
        result.UnrecapturedSection1250Gain.Should().Be(215_000m);
    }
}
