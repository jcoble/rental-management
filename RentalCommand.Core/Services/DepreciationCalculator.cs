using RentalCommand.Core.Enums;

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
    public const decimal ResidentialRecoveryYears = RecoveryClass.ResidentialBuilding;

    private static readonly decimal[] MacrsFiveYearHalfYear =
    [
        0.20m,
        0.32m,
        0.192m,
        0.1152m,
        0.1152m,
        0.0576m,
    ];

    private static readonly decimal[] MacrsSevenYearHalfYear =
    [
        0.1429m,
        0.2449m,
        0.1749m,
        0.1249m,
        0.0893m,
        0.0892m,
        0.0893m,
        0.0446m,
    ];

    private static readonly decimal[] MacrsFifteenYearHalfYear =
    [
        0.05m,
        0.095m,
        0.0855m,
        0.077m,
        0.0693m,
        0.0623m,
        0.059m,
        0.059m,
        0.0591m,
        0.059m,
        0.0591m,
        0.059m,
        0.0591m,
        0.059m,
        0.0591m,
        0.0295m,
    ];

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

        return AnnualForYear(
            building,
            inService,
            DepreciationMethod.StraightLine,
            ResidentialRecoveryYears,
            DepreciationConvention.MidMonth,
            basis.AccumulatedDepreciation,
            year);
    }

    public static DepreciationResult AnnualForYear(
        decimal costBasis,
        DateTime inServiceDate,
        DepreciationMethod method,
        decimal recoveryYears,
        DepreciationConvention convention,
        decimal accumulatedDepreciation,
        int year)
    {
        var basis = Round(costBasis);
        if (basis <= 0m || recoveryYears <= 0m || year < inServiceDate.Year)
            return new DepreciationResult(0m, false);

        var remaining = Math.Max(0m, basis - accumulatedDepreciation);
        if (remaining <= 0m)
            return new DepreciationResult(0m, false);

        var result = method switch
        {
            DepreciationMethod.StraightLine => StraightLineAnnual(basis, inServiceDate, recoveryYears, convention, year),
            DepreciationMethod.Macrs => MacrsAnnual(basis, inServiceDate, recoveryYears, convention, year),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported depreciation method."),
        };

        var computed = Math.Min(result.Amount, remaining);
        return new DepreciationResult(Math.Max(0m, computed), result.IsFirstYearEstimate);
    }

    public static decimal UnrecapturedSec1250Gain(decimal totalGain, decimal accumulatedDepreciation)
    {
        if (totalGain <= 0m || accumulatedDepreciation <= 0m)
            return 0m;

        return Round(Math.Min(totalGain, accumulatedDepreciation));
    }

    private static DepreciationResult StraightLineAnnual(
        decimal costBasis,
        DateTime inServiceDate,
        decimal recoveryYears,
        DepreciationConvention convention,
        int year)
    {
        var fullYear = costBasis / recoveryYears;
        var yearIndex = year - inServiceDate.Year;

        return convention switch
        {
            DepreciationConvention.MidMonth => StraightLineMidMonth(fullYear, inServiceDate, year),
            DepreciationConvention.HalfYear => StraightLineHalfYear(fullYear, recoveryYears, yearIndex),
            _ => throw new ArgumentOutOfRangeException(nameof(convention), convention, "Unsupported straight-line depreciation convention."),
        };
    }

    private static DepreciationResult StraightLineMidMonth(decimal fullYear, DateTime inServiceDate, int year)
    {
        decimal monthsInService;
        bool firstYearEstimate;
        if (year == inServiceDate.Year)
        {
            // IRS mid-month approximation: whole months strictly AFTER the in-service month, + 0.5.
            var monthsAfter = 12 - inServiceDate.Month; // e.g. July(7) -> 5 (Aug..Dec)
            monthsInService = monthsAfter + 0.5m;
            firstYearEstimate = true;
        }
        else
        {
            monthsInService = 12m;
            firstYearEstimate = false;
        }

        return new DepreciationResult(Round(fullYear * monthsInService / 12m), firstYearEstimate);
    }

    private static DepreciationResult StraightLineHalfYear(decimal fullYear, decimal recoveryYears, int yearIndex)
    {
        if (yearIndex < 0 || yearIndex > recoveryYears)
            return new DepreciationResult(0m, false);

        var multiplier = yearIndex == 0 || yearIndex == recoveryYears ? 0.5m : 1m;
        return new DepreciationResult(Round(fullYear * multiplier), yearIndex == 0);
    }

    private static DepreciationResult MacrsAnnual(
        decimal costBasis,
        DateTime inServiceDate,
        decimal recoveryYears,
        DepreciationConvention convention,
        int year)
    {
        if (convention != DepreciationConvention.HalfYear)
            throw new ArgumentOutOfRangeException(nameof(convention), convention, "Only half-year MACRS is supported.");

        var yearIndex = year - inServiceDate.Year;
        var table = MacrsHalfYearTable(recoveryYears);
        if (yearIndex < 0 || yearIndex >= table.Length)
            return new DepreciationResult(0m, false);

        return new DepreciationResult(Round(costBasis * table[yearIndex]), yearIndex == 0);
    }

    private static decimal[] MacrsHalfYearTable(decimal recoveryYears) => recoveryYears switch
    {
        RecoveryClass.Appliance => MacrsFiveYearHalfYear,
        RecoveryClass.Furniture => MacrsSevenYearHalfYear,
        RecoveryClass.LandImprovement => MacrsFifteenYearHalfYear,
        _ => throw new ArgumentOutOfRangeException(nameof(recoveryYears), recoveryYears, "Unsupported MACRS recovery class."),
    };

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
