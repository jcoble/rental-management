using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Reports Hub DTOs (read-only). The Reports Hub is a thin reporting layer over existing data: every
// report here is a pure query that projects to one of these clean, serializable shapes. There are no
// create/update/delete operations. All reports are portfolio-scoped (scope comes from the JWT claim,
// never a request parameter) and optionally filtered by property and/or a date range.
//
// Money amounts are decimals. Dates are UTC (Postgres timestamptz). Enums serialize as string names
// (JsonStringEnumConverter is registered app-wide), so the string-typed *Name fields here exist purely
// for convenient display/test selectors.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

// ── Catalog ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The full reports catalog: every available report grouped by category, so the web can render the
/// report cards and the generic parameter bar without hard-coding the report list.
/// </summary>
public class ReportsCatalogResponse
{
    public IReadOnlyList<ReportCategoryGroup> Categories { get; set; } = [];
}

/// <summary>One catalog category ("Accounting", "Rent &amp; Payments", "Owners", "Operations") and its reports.</summary>
public class ReportCategoryGroup
{
    /// <summary>Stable category key, e.g. "accounting".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human title, e.g. "Rent &amp; Payments".</summary>
    public string Title { get; set; } = string.Empty;

    public IReadOnlyList<ReportCatalogEntry> Reports { get; set; } = [];
}

/// <summary>A single report in the catalog: how the web addresses it, describes it, and which params it accepts.</summary>
public class ReportCatalogEntry
{
    /// <summary>Stable report key, e.g. "rent-roll". Used to address the report in the UI/router.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Report title, e.g. "Rent Roll".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One-line description for the card.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The endpoint path the web should call to generate this report (relative to the API root,
    /// e.g. "/api/v1/reports/rent-roll"). For reports that live on another existing controller
    /// (Schedule E, Owner Statement, Year-End Packet) this points at that existing endpoint so the
    /// hub deep-links instead of reimplementing.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Which parameters this report accepts, so the web can render the param bar generically. Each
    /// entry is one of the well-known param keys in <see cref="ReportParamKeys"/>.
    /// </summary>
    public IReadOnlyList<string> Params { get; set; } = [];

    /// <summary>
    /// True when this report is served by a pre-existing endpoint on another controller rather than a
    /// new ReportsController action (the hub references it, does not reimplement it).
    /// </summary>
    public bool External { get; set; }
}

/// <summary>Well-known parameter keys a report can declare in its catalog entry.</summary>
public static class ReportParamKeys
{
    /// <summary>Date-range start (inclusive), bound from query <c>from</c>.</summary>
    public const string From = "from";

    /// <summary>Date-range end (inclusive), bound from query <c>to</c>.</summary>
    public const string To = "to";

    /// <summary>Single property filter, bound from query <c>propertyId</c>.</summary>
    public const string PropertyId = "propertyId";

    /// <summary>Multi-property filter, bound from repeated query <c>propertyIds</c>.</summary>
    public const string PropertyIds = "propertyIds";

    /// <summary>Tax year, bound from query <c>year</c> (Schedule E, owner statement, year-end packet, 1099).</summary>
    public const string Year = "year";

    /// <summary>Owner id, bound from query <c>ownerId</c> (owner statement).</summary>
    public const string OwnerId = "ownerId";

    /// <summary>Forward-looking window in days, bound from query <c>days</c> (lease expirations).</summary>
    public const string Days = "days";
}

// ── Shared param binding ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Common report query parameters bound from the query string. Portfolio scope is implicit (claim).
/// All four fields are optional; each report uses the subset that applies to it. The service is
/// responsible for IDOR-validating <see cref="PropertyId"/>/<see cref="PropertyIds"/> against the
/// portfolio and for coercing the dates to UTC before they reach Npgsql.
/// </summary>
public class ReportRangeQuery
{
    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "from")]
    public DateTime? From { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "to")]
    public DateTime? To { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "propertyIds")]
    public List<int>? PropertyIds { get; set; }
}

// ── Rent Roll ────────────────────────────────────────────────────────────────────────────────────

/// <summary>Current rent-roll snapshot: one row per active/under-notice lease, plus portfolio totals.</summary>
public class RentRollResponse
{
    public DateTime GeneratedAt { get; set; }
    public IReadOnlyList<RentRollRow> Rows { get; set; } = [];

    /// <summary>Count of leases in the roll.</summary>
    public int LeaseCount { get; set; }

    /// <summary>Sum of <see cref="RentRollRow.MonthlyRent"/> across all rows.</summary>
    public decimal TotalMonthlyRent { get; set; }

    /// <summary>Sum of <see cref="RentRollRow.SecurityDeposit"/> across all rows.</summary>
    public decimal TotalSecurityDeposit { get; set; }
}

public class RentRollRow
{
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public int AgreementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public string AgreementNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }
    public DateOnly StartOn { get; set; }
    public DateOnly? EndOn { get; set; }
    public string StatusName { get; set; } = string.Empty;
}

// ── Rent Ledger (accrual, per lease over a range) ─────────────────────────────────────────────────

/// <summary>
/// Accrual rent ledger across the portfolio for a date range: charges due vs. payments received,
/// grouped by continuous lease-management relationship, with a running balance per tenant account.
/// A positive balance means the tenant owes.
/// </summary>
public class RentLedgerResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<RentLedgerLease> Leases { get; set; } = [];

    /// <summary>Sum of all charges due in the range across every lease.</summary>
    public decimal TotalCharged { get; set; }

    /// <summary>Sum of all payments received in the range across every lease.</summary>
    public decimal TotalCredits { get; set; }

    /// <summary>Closing balance across every tenant account (TotalCharged minus TotalCredits).</summary>
    public decimal TotalBalance { get; set; }
}

/// <summary>One lease-management relationship's ordered ledger entries and running totals.</summary>
public class RentLedgerLease
{
    public int LeaseManagementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public IReadOnlyList<RentLedgerEntry> Entries { get; set; } = [];
    public decimal TotalCharged { get; set; }
    public decimal TotalCredits { get; set; }

    /// <summary>Closing running balance for this tenant account. Positive means owed.</summary>
    public decimal Balance { get; set; }
}

/// <summary>One charge or payment in a rent ledger, with the running balance after it is applied.</summary>
public class RentLedgerEntry
{
    public DateTime Date { get; set; }

    /// <summary>"Charge", "Receipt", or another explicit ledger credit type.</summary>
    public string Type { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>The amount charged on this entry (0 for a payment).</summary>
    public decimal Charge { get; set; }

    /// <summary>The amount paid on this entry (0 for a charge).</summary>
    public decimal Credit { get; set; }

    /// <summary>Running balance owed after this entry (Σcharges − Σpayments up to and including this row).</summary>
    public decimal Balance { get; set; }
}

// ── Delinquency / Overdue Aging ──────────────────────────────────────────────────────────────────

/// <summary>Outstanding balances aged into buckets per lease-management relationship plus totals.</summary>
public class DelinquencyResponse
{
    public DateTime AsOf { get; set; }
    public IReadOnlyList<DelinquencyRow> Rows { get; set; } = [];
    public DelinquencyBuckets Totals { get; set; } = new();

    /// <summary>Grand total outstanding across all buckets and leases.</summary>
    public decimal TotalOutstanding { get; set; }
}

/// <summary>One delinquent lease-management relationship/tenant aged into buckets.</summary>
public class DelinquencyRow
{
    public int LeaseManagementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public DelinquencyBuckets Buckets { get; set; } = new();

    /// <summary>Total owed for this lease across all buckets.</summary>
    public decimal Total { get; set; }

    /// <summary>Age in days of the oldest unpaid charge (drives which bucket the row "leads" with).</summary>
    public int OldestOverdueDays { get; set; }
}

/// <summary>Aging buckets by days past due.</summary>
public class DelinquencyBuckets
{
    /// <summary>0–30 days past due.</summary>
    public decimal Current { get; set; }

    /// <summary>31–60 days past due.</summary>
    public decimal Days31To60 { get; set; }

    /// <summary>61–90 days past due.</summary>
    public decimal Days61To90 { get; set; }

    /// <summary>More than 90 days past due.</summary>
    public decimal Over90 { get; set; }
}

// ── Cash Flow (income vs expense by month) ───────────────────────────────────────────────────────

/// <summary>Income vs. expense by month over a range, with per-month net and grand totals.</summary>
public class CashFlowResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<CashFlowMonth> Months { get; set; } = [];
    public decimal TotalIncome { get; set; }
    public decimal TotalExpense { get; set; }
    public decimal TotalNet { get; set; }
}

/// <summary>One calendar month of cash flow.</summary>
public class CashFlowMonth
{
    /// <summary>Year of the month.</summary>
    public int Year { get; set; }

    /// <summary>Month number 1–12.</summary>
    public int Month { get; set; }

    /// <summary>"yyyy-MM" key for stable sorting/joining on the client.</summary>
    public string MonthKey { get; set; } = string.Empty;

    /// <summary>Abbreviated month + year label, e.g. "Jun 2026".</summary>
    public string Label { get; set; } = string.Empty;

    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Net { get; set; }
}

// ── General Ledger / Account Transactions (running balance) ──────────────────────────────────────

/// <summary>
/// General ledger: every recognized tenant receipt and expense over the range in date order.
/// Income is positive, expense is negative; the running balance is the cumulative net.
/// </summary>
public class GeneralLedgerResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<GeneralLedgerEntry> Entries { get; set; } = [];
    public decimal TotalIncome { get; set; }
    public decimal TotalExpense { get; set; }

    /// <summary>Closing running balance (TotalIncome − TotalExpense).</summary>
    public decimal ClosingBalance { get; set; }
}

public class GeneralLedgerEntry
{
    public DateTime Date { get; set; }

    /// <summary>"Receipt" or "Expense".</summary>
    public string Type { get; set; } = string.Empty;

    public long Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public string? Counterparty { get; set; }

    /// <summary>Signed amount: positive for income (collected payment), negative for an expense.</summary>
    public decimal Amount { get; set; }

    /// <summary>Cumulative net balance after applying this entry.</summary>
    public decimal RunningBalance { get; set; }
}

// ── Property P&L Summary ─────────────────────────────────────────────────────────────────────────

/// <summary>Income / expense / net per property over the range, plus portfolio totals.</summary>
public class PropertyProfitAndLossResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<PropertyProfitAndLossRow> Rows { get; set; } = [];
    public decimal TotalIncome { get; set; }
    public decimal TotalExpense { get; set; }
    public decimal TotalNet { get; set; }
}

public class PropertyProfitAndLossRow
{
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal Net { get; set; }
}

// ── Occupancy / Vacancy ──────────────────────────────────────────────────────────────────────────

/// <summary>Per-property unit occupancy, plus portfolio-level totals and occupancy %.</summary>
public class OccupancyResponse
{
    public DateTime GeneratedAt { get; set; }
    public IReadOnlyList<OccupancyRow> Rows { get; set; } = [];
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    public int VacantUnits { get; set; }

    /// <summary>Portfolio occupancy percentage (0–100), occupied ÷ total. 0 when there are no units.</summary>
    public decimal OccupancyPercent { get; set; }
}

public class OccupancyRow
{
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    public int VacantUnits { get; set; }

    /// <summary>Occupancy percentage for this property (0–100). 0 when the property has no units.</summary>
    public decimal OccupancyPercent { get; set; }
}

// ── Lease Expirations / Renewals Due ─────────────────────────────────────────────────────────────

/// <summary>Active/under-notice leases whose end date falls within the next N days.</summary>
public class LeaseExpirationsResponse
{
    public DateOnly AsOf { get; set; }

    /// <summary>The forward-looking window in days that was applied.</summary>
    public int WindowDays { get; set; }

    public IReadOnlyList<LeaseExpirationRow> Rows { get; set; } = [];
    public int LeaseCount { get; set; }
    public decimal TotalMonthlyRent { get; set; }
}

public class LeaseExpirationRow
{
    public int LeaseManagementId { get; set; }
    public int AgreementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public string AgreementNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public decimal MonthlyRent { get; set; }
    public DateOnly EndOn { get; set; }

    /// <summary>Days from "now" until the lease ends (negative if already past, which can happen for NoticeGiven leases).</summary>
    public int DaysUntilExpiry { get; set; }

    public string StatusName { get; set; } = string.Empty;
}

// ── Security Deposit Register ─────────────────────────────────────────────────────────────────────

/// <summary>Per-relationship security-deposit holdings: held, deductions, returned, current balance + totals.</summary>
public class SecurityDepositRegisterResponse
{
    public DateTime GeneratedAt { get; set; }
    public IReadOnlyList<SecurityDepositRegisterRow> Rows { get; set; } = [];
    public decimal TotalHeld { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalReturned { get; set; }

    /// <summary>Sum of <see cref="SecurityDepositRegisterRow.CurrentBalance"/> — money still held in trust.</summary>
    public decimal TotalCurrentBalance { get; set; }
}

public class SecurityDepositRegisterRow
{
    public int DepositId { get; set; }
    public int LeaseManagementId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;

    /// <summary>Original amount held.</summary>
    public decimal Held { get; set; }

    /// <summary>Total of recorded deductions against the deposit.</summary>
    public decimal Deductions { get; set; }

    /// <summary>Amount actually returned to the tenant (0 until a return is recorded).</summary>
    public decimal Returned { get; set; }

    /// <summary>What is still held in trust right now: Held − Deductions − Returned (floored at 0).</summary>
    public decimal CurrentBalance { get; set; }

    /// <summary>Database-derived status: NotFunded, Held, Withheld, PartiallyReturned, or Returned.</summary>
    public string Status { get; set; } = string.Empty;
    public string StatusName { get; set; } = string.Empty;
    public DateTime HeldAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
}

// ── Vendor 1099 & Payments ───────────────────────────────────────────────────────────────────────

/// <summary>1099-eligible vendors (and any vendor paid in the year): total paid, W-9 status, review flags.</summary>
public class Vendor1099Response
{
    public int Year { get; set; }
    public IReadOnlyList<Vendor1099Row> Rows { get; set; } = [];

    /// <summary>Sum of <see cref="Vendor1099Row.TotalPaid"/> across all rows.</summary>
    public decimal TotalPaid { get; set; }

    /// <summary>The IRS 1099 reporting threshold applied to flag "needs review" (currently $600).</summary>
    public decimal Threshold { get; set; }
}

public class Vendor1099Row
{
    public int VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public string? TaxId { get; set; }

    /// <summary>Total expenses paid to this vendor whose PaidAt falls in the year.</summary>
    public decimal TotalPaid { get; set; }

    public bool Is1099Eligible { get; set; }
    public bool W9OnFile { get; set; }

    /// <summary>1099-eligible but no W-9 on file — collect one before filing.</summary>
    public bool NeedsW9 { get; set; }

    /// <summary>1099-eligible and paid at or above the reporting threshold — a 1099-NEC is likely due.</summary>
    public bool Needs1099Review { get; set; }
}

// ── Owner Distributions ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-owner net proceeds, recorded owner distributions, and remaining undistributed owner balance
/// for a year. Net per owner is the sum across the owner's properties of
/// (rental income − expenses − management fee), matching the owner statement.
/// </summary>
public class OwnerDistributionsResponse
{
    public int Year { get; set; }
    public IReadOnlyList<OwnerDistributionRow> Rows { get; set; } = [];

    /// <summary>Sum of net distributions across all owners.</summary>
    public decimal TotalNetToOwners { get; set; }

    public decimal TotalDistributed { get; set; }
    public decimal TotalUndistributed { get; set; }
}

public class OwnerDistributionRow
{
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public decimal NetToOwner { get; set; }
    public decimal TotalDistributed { get; set; }
    public decimal Undistributed { get; set; }
}

// ── Work Orders / Maintenance ────────────────────────────────────────────────────────────────────

/// <summary>Work orders requested in a date range, with per-status counts and a cost rollup.</summary>
public class WorkOrderReportResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public IReadOnlyList<WorkOrderReportRow> Rows { get; set; } = [];
    public int TotalCount { get; set; }
    public int OpenCount { get; set; }
    public int CompletedCount { get; set; }

    /// <summary>Sum of recorded actual cost across the listed work orders.</summary>
    public decimal TotalActualCost { get; set; }
}

public class WorkOrderReportRow
{
    public int WorkOrderId { get; set; }
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string? UnitNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public WorkOrderPriority Priority { get; set; }
    public string PriorityName { get; set; } = string.Empty;
    public WorkOrderStatus Status { get; set; }
    public string StatusName { get; set; } = string.Empty;
    public string? VendorName { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public decimal? ActualCost { get; set; }
}
