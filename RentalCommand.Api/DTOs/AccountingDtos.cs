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

    /// <summary>Plain-English money snapshot suitable for dashboard/accounting cards.</summary>
    public MoneySnapshotCardResponse Snapshot { get; set; } = new();

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

/// <summary>
/// Short title/summary/bullets card shown on the accounting workspace ("Money Snapshot" card).
/// This is the narrative card variant; <see cref="MoneySnapshotResponse"/> is the figures-first
/// snapshot used by the dashboard and daily briefing.
/// </summary>
public class MoneySnapshotCardResponse
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<string> Bullets { get; set; } = [];
}

/// <summary>
/// Plain-English money snapshot for a non-technical landlord: money in, money out, what's kept, and
/// who's behind — each paired with a one-sentence explanation that needs no accounting knowledge.
/// Shaped for a one-glance dashboard card and the daily briefing. Figures are simple aggregates over
/// the current period (month-to-date), with a trailing-30-day cut alongside for context.
/// </summary>
public class MoneySnapshotResponse
{
    public int PortfolioId { get; set; }

    /// <summary>Human label for the current period, e.g. "June 2026 (so far)".</summary>
    public string PeriodLabel { get; set; } = string.Empty;

    /// <summary>First day of the current period (month-to-date start), UTC.</summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>"As of" timestamp the figures were computed (period end), UTC.</summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>Money in: payments collected during the current period.</summary>
    public decimal Collected { get; set; }

    /// <summary>Money out: expenses paid/incurred during the current period.</summary>
    public decimal Spent { get; set; }

    /// <summary>What's kept: <see cref="Collected"/> minus <see cref="Spent"/> (can be negative).</summary>
    public decimal Net { get; set; }

    /// <summary>Total amount past due across all unpaid rent/charges (not period-bound).</summary>
    public decimal PastDueAmount { get; set; }

    /// <summary>Number of tenants currently behind (at least one past-due payment).</summary>
    public int PastDueCount { get; set; }

    /// <summary>Money collected over the trailing 30 days (rolling context alongside MTD).</summary>
    public decimal CollectedLast30Days { get; set; }

    /// <summary>Money spent over the trailing 30 days (rolling context alongside MTD).</summary>
    public decimal SpentLast30Days { get; set; }

    /// <summary>Net kept over the trailing 30 days.</summary>
    public decimal NetLast30Days { get; set; }

    /// <summary>One plain-English sentence per figure, for a non-technical reader.</summary>
    public MoneySnapshotExplanations Explanations { get; set; } = new();
}

/// <summary>One-sentence, jargon-free explanation of each figure in <see cref="MoneySnapshotResponse"/>.</summary>
public class MoneySnapshotExplanations
{
    public string Collected { get; set; } = string.Empty;
    public string Spent { get; set; } = string.Empty;
    public string Net { get; set; } = string.Empty;
    public string PastDue { get; set; } = string.Empty;
}

/// <summary>
/// Broader accounting workspace data: ledger entries, property-level P&amp;L, Schedule E totals, and
/// vendor 1099 review status.
/// </summary>
public class AccountingReportsResponse
{
    public int PortfolioId { get; set; }
    public DateTime GeneratedAt { get; set; }
    public decimal TotalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal NetCashFlow { get; set; }
    public IReadOnlyList<LedgerTransactionResponse> Ledger { get; set; } = [];
    public IReadOnlyList<PropertyFinancialSummaryResponse> Properties { get; set; } = [];
    public IReadOnlyList<ScheduleECategoryTotal> ScheduleE { get; set; } = [];
    public IReadOnlyList<Vendor1099SummaryResponse> Vendors1099 { get; set; } = [];
}

public class AccountingTransactionsQuery : ListQuery
{
    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "kind")]
    public string? Kind { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "status")]
    public string? Status { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "category")]
    public string? Category { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "from")]
    public DateTime? From { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "to")]
    public DateTime? To { get; set; }
}

public class AccountingTransactionsResponse
{
    public IReadOnlyList<AccountingTransactionResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class AccountingTransactionResponse
{
    public string Kind { get; set; } = string.Empty;
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public string? Counterparty { get; set; }
    public string? DetailHref { get; set; }
    public bool HasReceipt { get; set; }
    public bool ReceiptIsImage { get; set; }
}

public class LedgerTransactionResponse
{
    public DateTime Date { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public string? Counterparty { get; set; }
    public string? Category { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SourceHref { get; set; } = string.Empty;

    /// <summary>
    /// Plain-English "why this is here", derived deterministically from the entry's type/date/amount/
    /// status — no LLM. Answers a tenant's "what is this charge?" without accounting knowledge,
    /// e.g. "Rent for March 2026" or "Payment received by check on Mar 3".
    /// </summary>
    public string Explanation { get; set; } = string.Empty;
}

public class PropertyFinancialSummaryResponse
{
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public decimal Income { get; set; }
    public decimal Expenses { get; set; }
    public decimal Net { get; set; }
    public decimal Overdue { get; set; }
    public int OverdueCount { get; set; }
}

public class Vendor1099SummaryResponse
{
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public decimal TotalPaid { get; set; }
    public bool Is1099Eligible { get; set; }
    public bool W9OnFile { get; set; }
    public bool NeedsW9 { get; set; }
    public bool Needs1099Review { get; set; }
}
