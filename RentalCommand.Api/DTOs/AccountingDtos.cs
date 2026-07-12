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
/// The "Who's behind" list: one row per lease/tenant currently behind on rent, plus the headline
/// totals. This is the actionable destination behind the dashboard "tenants behind" KPI — the same
/// past-due definition powers both, so <see cref="TotalCount"/> always equals the KPI count and the
/// number of <see cref="Items"/>. Computed DB-side in a single grouped query (no rows loaded to count).
/// </summary>
public class PastDueResponse
{
    /// <summary>One row per behind lease, ordered by who's been waiting longest (oldest due date first).</summary>
    public IReadOnlyList<PastDueLeaseResponse> Items { get; set; } = [];

    /// <summary>Number of leases/tenants behind — equal to <see cref="Items"/>.Count and the KPI's PastDueCount.</summary>
    public int TotalCount { get; set; }

    /// <summary>Total amount past due across all behind leases — equal to the KPI's PastDueAmount.</summary>
    public decimal TotalPastDueAmount { get; set; }
}

/// <summary>
/// One lease/tenant that is behind on rent, with everything the landlord needs to act: who they are,
/// how much they owe, how many payments are past due, how long they've been late, and a deep-link
/// anchor to the oldest past-due payment.
/// </summary>
public class PastDueLeaseResponse
{
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public int? CurrentAgreementId { get; set; }

    /// <summary>Unit id on the behind lease, so the "open oldest payment" link can deep-link into the
    /// unit's Command Center Rent tab rather than the generic payment detail page.</summary>
    public int UnitId { get; set; }

    /// <summary>Tenant name on the behind lease (e.g. "Maria Tenant"), or null when not set.</summary>
    public string? TenantName { get; set; }

    /// <summary>Tenant phone, for a one-tap reminder text; null when not on file.</summary>
    public string? TenantPhone { get; set; }

    /// <summary>Human lease number (e.g. "L-001").</summary>
    public string? RelationshipNumber { get; set; }

    /// <summary>Property name for context in the list.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Unit number for context in the list.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Total amount this lease is behind (sum of its past-due payments).</summary>
    public decimal PastDueAmount { get; set; }

    /// <summary>How many of this lease's payments are past due.</summary>
    public int OverduePaymentCount { get; set; }

    /// <summary>Due date of this lease's oldest past-due payment (drives the "N days late" label).</summary>
    public DateOnly OldestDueOn { get; set; }

    /// <summary>Id of this lease's oldest past-due payment, so the row can deep-link into its detail.</summary>
    public long OldestLedgerEntryId { get; set; }
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
    public int LedgerTotalCount { get; set; }
    public IReadOnlyList<LedgerTransactionResponse> RecentLedger { get; set; } = [];
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

    // From/To (the ?from=&to= date range) are inherited from ListQuery now, so the grid date filter is
    // uniform across every list endpoint. AccountingService keeps applying them to the transaction date.
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

    /// <summary>When the row entered the system (created). Powers the ledger's "Entered" column and the
    /// default newest-entered-first sort (<c>sort=-createdAt</c>).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the row was last edited; equals <see cref="CreatedAt"/> for untouched rows.</summary>
    public DateTime UpdatedAt { get; set; }

    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int? PropertyId { get; set; }

    /// <summary>Unit id for the row (via Lease for payments, direct for expenses; null for bank rows).
    /// Lets the client route Payment/Expense ledger rows into their unit's Command Center tab.</summary>
    public int? UnitId { get; set; }

    public string? PropertyName { get; set; }
    public string? Counterparty { get; set; }
    public string? DetailHref { get; set; }
    public bool HasReceipt { get; set; }
    public bool ReceiptIsImage { get; set; }

    /// <summary>
    /// True when a bank transaction has been confirmed (Matched) against this Payment/Expense row,
    /// i.e. it has "cleared" the bank. Always false for the bank rows themselves. Powers the inline
    /// "✓ Cleared" badge on the accounting ledger.
    /// </summary>
    public bool Reconciled { get; set; }

    /// <summary>Bank/institution name of the matched bank line, when <see cref="Reconciled"/> is true.</summary>
    public string? ClearedBankName { get; set; }

    /// <summary>Posted date of the matched bank line, when <see cref="Reconciled"/> is true.</summary>
    public DateTime? ClearedAt { get; set; }

    /// <summary>
    /// A high-confidence, still-unmatched bank line the user could one-tap confirm against this
    /// Payment/Expense row. Populated only when the row is NOT already <see cref="Reconciled"/> and a
    /// suggestion exists; null otherwise. Powers the "Match?" chip. Confirming is never automatic.
    /// </summary>
    public SuggestedBankMatchResponse? SuggestedBankMatch { get; set; }
}

/// <summary>
/// A suggested (unconfirmed) bank line for a Payment/Expense row on the accounting ledger. The user
/// confirms it with one tap; nothing is auto-matched.
/// </summary>
public class SuggestedBankMatchResponse
{
    /// <summary>Id of the suggested <see cref="RentalCommand.Core.Entities.BankTransaction"/>.</summary>
    public int BankTransactionId { get; set; }

    /// <summary>Best display name for the suggested bank line (merchant, else institution).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Signed amount of the suggested bank line (deposits positive, withdrawals negative).</summary>
    public decimal Amount { get; set; }

    /// <summary>Posted date of the suggested bank line.</summary>
    public DateTime Date { get; set; }

    /// <summary>Match confidence in (0,1] from the banking match engine.</summary>
    public decimal Confidence { get; set; }
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

    /// <summary>True when this rent row was reduced for a partial first/final billing period.</summary>
    public bool IsProrated { get; set; }

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
