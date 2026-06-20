namespace RentalCommand.Core.Services;

/// <summary>
/// The depreciation-relevant fields of a property (spec §4/§18). Land is not depreciable, so the
/// building basis is <see cref="PurchasePrice"/> − <see cref="LandValue"/>.
/// </summary>
/// <param name="PurchasePrice">Acquisition cost; null when depreciation is not tracked.</param>
/// <param name="LandValue">Portion allocated to land (not depreciable); null treated as 0.</param>
/// <param name="InServiceDate">Date placed in service; drives the first-year mid-month proration.</param>
/// <param name="ManualAnnualDepreciation">Override; wins over the computed figure when set.</param>
/// <param name="AccumulatedDepreciation">Cumulative depreciation already taken (caps the result at basis).</param>
public readonly record struct PropertyDepreciationBasis(
    decimal? PurchasePrice,
    decimal? LandValue,
    DateTime? InServiceDate,
    decimal? ManualAnnualDepreciation,
    decimal AccumulatedDepreciation);

/// <summary>
/// One year's depreciation figure plus whether it is the first-year (placed-in-service) estimate,
/// which the IRS mid-month convention only approximates — the UI labels it "confirm with accountant".
/// </summary>
public readonly record struct DepreciationResult(decimal Amount, bool IsFirstYearEstimate);

/// <summary>
/// Pure straight-line residential depreciation (27.5-year) with the §18 corrections. No I/O — every
/// figure is decimal, rounded to cents. The cumulative total can never exceed the building basis;
/// the in-service year is prorated with the IRS mid-month approximation.
/// </summary>
public static class DepreciationCalculator
{
    /// <summary>Residential rental property recovery period, in years (IRS straight-line).</summary>
    public const decimal ResidentialRecoveryYears = 27.5m;

    /// <summary>
    /// Annual depreciation for <paramref name="basis"/> in tax <paramref name="year"/> (spec §6/§18):
    /// building basis ÷ 27.5, prorated in the in-service year by the IRS mid-month convention
    /// (whole months strictly after the in-service month, + 0.5), with a manual override winning and
    /// the cumulative total capped at the building basis via <see cref="PropertyDepreciationBasis.AccumulatedDepreciation"/>.
    /// </summary>
    public static DepreciationResult AnnualForYear(PropertyDepreciationBasis basis, int year)
    {
        // Building basis (land is not depreciable). Null/unknown purchase price => no computed basis.
        decimal? buildingBasis = basis.PurchasePrice.HasValue
            ? Round(basis.PurchasePrice.Value - (basis.LandValue ?? 0m))
            : (decimal?)null;

        // Remaining depreciable amount (the hard cap). When the basis is unknown we can't cap.
        decimal? remaining = buildingBasis.HasValue
            ? Math.Max(0m, buildingBasis.Value - basis.AccumulatedDepreciation)
            : (decimal?)null;

        // ── Manual override wins (still capped at remaining basis when the basis is known) ────────
        if (basis.ManualAnnualDepreciation is { } manual)
        {
            var amount = remaining.HasValue ? Math.Min(Round(manual), remaining.Value) : Round(manual);
            return new DepreciationResult(Math.Max(0m, amount), IsFirstYearEstimate: false);
        }

        // ── Computed straight-line ───────────────────────────────────────────────────────────────
        if (buildingBasis is not { } building || building <= 0m || basis.InServiceDate is not { } inService)
            return new DepreciationResult(0m, false);

        // Nothing before the property is placed in service; nothing once fully depreciated.
        if (year < inService.Year || remaining is <= 0m)
            return new DepreciationResult(0m, false);

        var fullYear = building / ResidentialRecoveryYears;

        decimal monthsInService;
        bool firstYearEstimate;
        if (year == inService.Year)
        {
            // IRS mid-month approximation: whole months strictly AFTER the in-service month, + 0.5.
            var monthsAfter = 12 - inService.Month; // e.g. July(7) -> 5 (Aug..Dec)
            monthsInService = monthsAfter + 0.5m;
            firstYearEstimate = true;
        }
        else
        {
            monthsInService = 12m;
            firstYearEstimate = false;
        }

        var computed = Round(fullYear * monthsInService / 12m);

        // Cap at the remaining basis so cumulative depreciation never exceeds the building basis.
        if (remaining is { } cap && computed > cap)
            computed = cap;

        return new DepreciationResult(Math.Max(0m, computed), firstYearEstimate);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
