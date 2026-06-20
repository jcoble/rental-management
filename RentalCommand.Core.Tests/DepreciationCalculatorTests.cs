using FluentAssertions;
using RentalCommand.Core.Services;

namespace RentalCommand.Core.Tests;

/// <summary>
/// Depreciation is the owner's biggest paper deduction and goes straight onto Schedule E, so these
/// tests pin spec §6 with the §18 corrections: building basis = purchase − land; straight-line over
/// 27.5 years; the in-service year uses the IRS mid-month approximation
/// (months strictly AFTER the in-service month, + 0.5); a manual override wins; and the cumulative
/// total can never exceed the building basis (via AccumulatedDepreciation).
/// </summary>
public class DepreciationCalculatorTests
{
    private static PropertyDepreciationBasis Basis(
        decimal? purchase = 300_000m,
        decimal? land = 60_000m,
        DateTime? inService = null,
        decimal? manual = null,
        decimal accumulated = 0m) =>
        new(purchase, land, inService, manual, accumulated);

    // ── Full year (after the first) ─────────────────────────────────────────────────────────────

    [Fact]
    public void FullYear_AfterInServiceYear_IsBasisOver27Point5()
    {
        // 300k − 60k land = 240k building basis. 240,000 / 27.5 = 8,727.2727… → 8,727.27.
        var basis = Basis(inService: new DateTime(2020, 1, 1));

        var result = DepreciationCalculator.AnnualForYear(basis, 2024);

        result.Amount.Should().Be(8_727.27m);
        result.IsFirstYearEstimate.Should().BeFalse();
    }

    // ── In-service year (IRS mid-month, §18) ────────────────────────────────────────────────────

    [Fact]
    public void InServiceYear_July_UsesMidMonth_FiveAndHalfMonths()
    {
        // Placed in service July → whole months strictly after July (Aug–Dec = 5) + 0.5 = 5.5.
        // 8,727.2727 * 5.5 / 12 = 4,000.00.
        var basis = Basis(inService: new DateTime(2024, 7, 10));

        var result = DepreciationCalculator.AnnualForYear(basis, 2024);

        result.Amount.Should().Be(4_000.00m);
        result.IsFirstYearEstimate.Should().BeTrue();
    }

    [Fact]
    public void InServiceYear_January_UsesMidMonth_ElevenAndHalfMonths()
    {
        // Placed in service January → 11 months after + 0.5 = 11.5. 8,727.2727 * 11.5 / 12 = 8,363.64.
        var basis = Basis(inService: new DateTime(2024, 1, 5));

        var result = DepreciationCalculator.AnnualForYear(basis, 2024);

        result.Amount.Should().Be(8_363.64m);
        result.IsFirstYearEstimate.Should().BeTrue();
    }

    [Fact]
    public void InServiceYear_December_UsesMidMonth_HalfMonth()
    {
        // Placed in service December → 0 months after + 0.5 = 0.5. 8,727.2727 * 0.5 / 12 = 363.64.
        var basis = Basis(inService: new DateTime(2024, 12, 20));

        var result = DepreciationCalculator.AnnualForYear(basis, 2024);

        result.Amount.Should().Be(363.64m);
        result.IsFirstYearEstimate.Should().BeTrue();
    }

    [Fact]
    public void BeforeInServiceYear_IsZero()
    {
        var basis = Basis(inService: new DateTime(2024, 6, 1));

        DepreciationCalculator.AnnualForYear(basis, 2023).Amount.Should().Be(0m);
    }

    // ── Manual override (§6) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ManualOverride_WinsOverComputed()
    {
        var basis = Basis(manual: 5_000m, inService: new DateTime(2020, 1, 1));

        var result = DepreciationCalculator.AnnualForYear(basis, 2024);

        result.Amount.Should().Be(5_000m);
    }

    [Fact]
    public void ManualOverride_WithoutBasis_StillReturnsManual()
    {
        // Owner entered only an override (no purchase price / in-service date).
        var basis = Basis(purchase: null, land: null, inService: null, manual: 7_200m);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(7_200m);
    }

    // ── Accumulated cap (§6/§18) ────────────────────────────────────────────────────────────────

    [Fact]
    public void AccumulatedNearBasis_ClampsRemainderToBasis()
    {
        // Building basis 240k; 238k already taken → only 2,000 may be taken this year (not 8,727.27).
        var basis = Basis(inService: new DateTime(2000, 1, 1), accumulated: 238_000m);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(2_000m);
    }

    [Fact]
    public void FullyDepreciated_IsZero()
    {
        var basis = Basis(inService: new DateTime(1990, 1, 1), accumulated: 240_000m);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(0m);
    }

    [Fact]
    public void ManualOverride_AlsoCappedAtRemainingBasis()
    {
        // Manual 5,000 but only 1,200 of basis remains → clamp to 1,200.
        var basis = Basis(inService: new DateTime(2000, 1, 1), manual: 5_000m, accumulated: 238_800m);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(1_200m);
    }

    // ── No basis at all ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoBasisAndNoOverride_IsZero()
    {
        var basis = Basis(purchase: null, land: null, inService: null, manual: null);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(0m);
    }

    [Fact]
    public void PurchaseWithoutInServiceDate_IsZero()
    {
        // Without an in-service date there is no placed-in-service year to depreciate from.
        var basis = Basis(inService: null);

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(0m);
    }

    [Fact]
    public void LandValueDefaultsToZeroWhenNull()
    {
        // No land allocation → whole purchase price is the building basis. 275,000 / 27.5 = 10,000.
        var basis = Basis(purchase: 275_000m, land: null, inService: new DateTime(2020, 1, 1));

        DepreciationCalculator.AnnualForYear(basis, 2024).Amount.Should().Be(10_000m);
    }
}
