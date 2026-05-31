using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// Read-only financial rollup for the caller's portfolio: expense totals grouped by IRS Schedule E
/// category plus rent-collection status (collected vs. still-owed/overdue). Shaped for a dashboard
/// summary; there is no create/update/delete for this resource.
/// </summary>
public class AccountingSummaryResponse
{
    public int PortfolioId { get; set; }

    /// <summary>Expense totals grouped by <see cref="ScheduleECategory"/> (active, non-deleted expenses).</summary>
    public IReadOnlyList<ScheduleECategoryTotal> ExpensesByCategory { get; set; } = [];

    /// <summary>Sum of all active expense amounts in the portfolio.</summary>
    public decimal TotalExpenses { get; set; }

    /// <summary>Rent-collection rollup across the portfolio's payments.</summary>
    public PaymentRollup Payments { get; set; } = new();

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => $"accounting-summary-{PortfolioId}";
}

/// <summary>One Schedule E category and the summed expense amount + count behind it.</summary>
public class ScheduleECategoryTotal
{
    public ScheduleECategory Category { get; set; }

    /// <summary>Category name as a string for convenient display/test selectors.</summary>
    public string CategoryName { get; set; } = string.Empty;

    public decimal Total { get; set; }
    public int Count { get; set; }
}

/// <summary>Rent/payment collection status rolled up for a portfolio.</summary>
public class PaymentRollup
{
    /// <summary>Sum of payments marked <see cref="PaymentStatus.Paid"/>.</summary>
    public decimal Collected { get; set; }

    /// <summary>Sum of payments still owed (Scheduled, Partial, or Late, due now or in the future).</summary>
    public decimal Outstanding { get; set; }

    /// <summary>Sum of payments past due and not paid (Late, or Scheduled/Partial with a DueDate in the past).</summary>
    public decimal Overdue { get; set; }

    /// <summary>Count of payments contributing to <see cref="Overdue"/>.</summary>
    public int OverdueCount { get; set; }
}
