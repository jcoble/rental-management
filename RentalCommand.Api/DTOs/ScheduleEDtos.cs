namespace RentalCommand.Api.DTOs;

/// <summary>A single IRS Schedule E expense category and its summed amount for a given year.</summary>
public record ScheduleECategoryAmount(string Category, decimal Amount);

/// <summary>
/// Tax activity that cannot yet be placed on an IRS Schedule E property line. This is populated only
/// for a workspace-wide Administrator; property-scoped callers receive the zero/default shape so the
/// existence of out-of-scope portfolio activity is never disclosed.
/// </summary>
public class ScheduleEUnallocatedActivity
{
    public bool CanView { get; set; }
    public bool RequiresAllocation { get; set; }
    public int IncomeEntryCount { get; set; }
    public decimal RentalIncome { get; set; }
    public int ExpenseCount { get; set; }
    public IReadOnlyList<ScheduleECategoryAmount> ExpensesByCategory { get; set; } = [];
    public decimal TotalExpenses { get; set; }
    public decimal NetIncome { get; set; }
    public string Warning { get; set; } = string.Empty;
}

/// <summary>
/// Year-end income/expense summary for a single property, structured for IRS Schedule E reporting.
/// <para><see cref="ExpensesByCategory"/> is the full deductible breakdown and already INCLUDES the
/// modeled <c>MortgageInterest</c> (from the loan split — principal excluded) and <c>Depreciation</c>
/// (computed from basis) lines; <see cref="TotalExpenses"/> sums them and <see cref="NetIncome"/> nets
/// them. <see cref="MortgageInterest"/> and <see cref="Depreciation"/> are surfaced separately so the
/// year-end view can label them (depreciation is non-cash; principal is never deductible).</para>
/// </summary>
public record ScheduleEPropertyReport(
    int PropertyId,
    string PropertyName,
    decimal RentalIncome,
    IReadOnlyList<ScheduleECategoryAmount> ExpensesByCategory,
    decimal TotalExpenses,
    decimal NetIncome,   // RentalIncome - TotalExpenses
    decimal MortgageInterest = 0m,
    decimal Depreciation = 0m,
    bool DepreciationIsFirstYearEstimate = false);

/// <summary>
/// Full Schedule E report for a portfolio for a given tax year: one row per property that had
/// any rental income or deductible expense, plus portfolio-level grand totals.
/// </summary>
public class ScheduleEReport
{
    public int Year { get; set; }
    public IReadOnlyList<ScheduleEPropertyReport> Properties { get; set; } = [];

    /// <summary>Allocated portfolio expense totals by IRS category, already aggregated in SQL.</summary>
    public IReadOnlyList<ScheduleECategoryAmount> ExpensesByCategory { get; set; } = [];

    /// <summary>Totals of property-allocated Schedule E rows. These remain the IRS per-property totals.</summary>
    public decimal TotalRentalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal NetIncome { get; set; }

    /// <summary>Explicit reconciliation for workspace activity that still needs property allocation.</summary>
    public ScheduleEUnallocatedActivity UnallocatedActivity { get; set; } = new();

    /// <summary>Allocated plus visible unallocated activity; never assigns shared activity to a property.</summary>
    public decimal ReconciledTotalRentalIncome { get; set; }
    public decimal ReconciledTotalExpenses { get; set; }
    public decimal ReconciledNetIncome { get; set; }
}
