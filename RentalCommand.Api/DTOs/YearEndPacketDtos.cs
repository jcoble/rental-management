namespace RentalCommand.Api.DTOs;

/// <summary>
/// All data the year-end packet PDF renders, gathered by <c>AccountingService.GetYearEndPacketAsync</c>.
/// This is the deliverable Frank hands his accountant: a Schedule E summary, per-property P&amp;L,
/// a month-by-month cash-flow summary, and a rent roll, all scoped to a single tax year.
/// Money is plain decimals; all figures derive from the same computations the live accounting
/// endpoints use, so the packet always reconciles with the on-screen reports.
/// </summary>
public class YearEndPacketData
{
    /// <summary>Tax year the packet covers.</summary>
    public int Year { get; set; }

    /// <summary>Portfolio display name (e.g. "Frank's Rentals").</summary>
    public string PortfolioName { get; set; } = string.Empty;

    /// <summary>Management company name shown on the cover, when set.</summary>
    public string ManagementCompanyName { get; set; } = string.Empty;

    /// <summary>When the packet was generated (UTC), shown on the cover.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>IRS Schedule E income/expense rollup for the year (reused from <c>ScheduleEService</c>).</summary>
    public ScheduleEReport ScheduleE { get; set; } = new();

    /// <summary>Per-property profit &amp; loss for the year.</summary>
    public IReadOnlyList<YearEndPropertyPnL> Properties { get; set; } = [];

    /// <summary>Month-by-month money in / money out / net for the year.</summary>
    public IReadOnlyList<YearEndCashFlowMonth> CashFlow { get; set; } = [];

    /// <summary>Year totals for the cash-flow summary.</summary>
    public decimal CashFlowMoneyIn { get; set; }
    public decimal CashFlowMoneyOut { get; set; }
    public decimal CashFlowNet { get; set; }

    /// <summary>Rent roll: one row per active lease (unit, tenant, rent, term, balance).</summary>
    public IReadOnlyList<YearEndRentRollRow> RentRoll { get; set; } = [];
}

/// <summary>Per-property profit &amp; loss for the tax year: rental income, expenses by category, net.</summary>
public class YearEndPropertyPnL
{
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public decimal Income { get; set; }

    /// <summary>Deductible expenses broken out by Schedule E category (zero categories omitted).</summary>
    public IReadOnlyList<ScheduleECategoryAmount> ExpensesByCategory { get; set; } = [];

    public decimal TotalExpenses { get; set; }
    public decimal Net { get; set; }
}

/// <summary>One calendar month of cash flow within the tax year.</summary>
public class YearEndCashFlowMonth
{
    /// <summary>Month number 1–12.</summary>
    public int Month { get; set; }

    /// <summary>Human label, e.g. "Jan".</summary>
    public string MonthName { get; set; } = string.Empty;

    /// <summary>Cash collected in the month (rent &amp; other paid payments).</summary>
    public decimal MoneyIn { get; set; }

    /// <summary>Cash spent in the month (expenses paid/incurred).</summary>
    public decimal MoneyOut { get; set; }

    /// <summary><see cref="MoneyIn"/> minus <see cref="MoneyOut"/>.</summary>
    public decimal Net { get; set; }
}

/// <summary>One rent-roll line: a unit's current tenant, rent, lease term, and balance.</summary>
public class YearEndRentRollRow
{
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public decimal MonthlyRent { get; set; }
    public DateTime LeaseStart { get; set; }
    public DateTime LeaseEnd { get; set; }
    public string LeaseStatus { get; set; } = string.Empty;

    /// <summary>Amount the tenant is currently past due (unpaid rent/charges due in the past). Zero if caught up.</summary>
    public decimal PastDueBalance { get; set; }
}
