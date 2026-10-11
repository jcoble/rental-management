using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only Reports Hub: a thin reporting layer over existing portfolio data. Every method is a pure
/// query that projects to a clean DTO — there is no create/update/delete here. All reports are scoped to
/// the caller's portfolio (the id comes from the JWT claim, never a request parameter) and validate any
/// inbound property filter against that portfolio (cross-tenant IDOR guard).
///
/// Reports already fully covered by an existing engine (Schedule E, Owner Statement, Year-End Packet)
/// are intentionally NOT duplicated here; the catalog references their existing endpoints instead. This
/// service adds the rent/occupancy/aging/deposit/operations queries plus a couple of range-scoped
/// accounting views (cash flow, general ledger, property P&amp;L, owner distributions, 1099) that the
/// existing accounting endpoints only expose lifetime-to-date or per-tax-year.
/// </summary>
public interface IReportsService
{
    /// <summary>The full catalog of available reports grouped by category, for the generic web UI.</summary>
    ReportsCatalogResponse GetCatalog();

    /// <summary>The portfolio business year used when annual report requests omit <c>year</c>.</summary>
    Task<int> GetDefaultAnnualReportYearAsync(WorkspaceReadScope scope, CancellationToken ct = default);

    /// <summary>Current rent-roll snapshot: one row per active/under-notice lease, plus totals.</summary>
    Task<RentRollResponse> GetRentRollAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Open tenant-account balances aged by charge date with property and portfolio rollups.</summary>
    Task<AgedReceivablesResponse> GetAgedReceivablesAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Accrual rent ledger per lease over the range: charges due vs. payments received, running balance.</summary>
    Task<RentLedgerResponse> GetRentLedgerAsync(
        WorkspaceReadScope access, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Outstanding balances aged into 0-30 / 31-60 / 61-90 / 90+ buckets per lease, with totals.</summary>
    Task<DelinquencyResponse> GetDelinquencyAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Income vs. expense by month over the range, with per-month net and grand totals.</summary>
    Task<CashFlowResponse> GetCashFlowAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>
    /// True cash flow per property + portfolio for the range (spec §9/§18): rent in − operating
    /// expenses (escrow-funded taxes/insurance excluded) − full debt service. Excludes deposits and
    /// non-cash depreciation; shows NOI and after-debt cash flow as distinct lines.
    /// </summary>
    Task<CashFlowSummaryResponse> GetTrueCashFlowAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>
    /// The year-end three-block view for a tax year (spec §11/§18): cash flow vs taxable income as
    /// distinct numbers, with depreciation + debt service present, plus the rent roll and the
    /// "see your accountant" caveats.
    /// </summary>
    Task<YearEndViewResponse> GetYearEndAsync(WorkspaceReadScope scope, int year, int? propertyId = null, CancellationToken ct = default);

    /// <summary>Every payment and expense over the range in date order with a running balance.</summary>
    Task<GeneralLedgerResponse> GetGeneralLedgerAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Income / expense / net per property over the range, plus portfolio totals.</summary>
    Task<PropertyProfitAndLossResponse> GetPropertyProfitAndLossAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Per-property unit occupancy/vacancy with occupancy %, plus portfolio totals.</summary>
    Task<OccupancyResponse> GetOccupancyAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Leases ending within the next <paramref name="days"/> days (default 90), with totals.</summary>
    Task<LeaseExpirationsResponse> GetLeaseExpirationsAsync(WorkspaceReadScope scope, ReportRangeQuery query, int days, CancellationToken ct = default);

    /// <summary>Per-lease security-deposit register: held / deductions / returned / current balance + totals.</summary>
    Task<SecurityDepositRegisterResponse> GetSecurityDepositRegisterAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);

    /// <summary>Per 1099-eligible vendor: total paid in <paramref name="year"/>, W-9 status, review flags.</summary>
    Task<Vendor1099Response> GetVendor1099Async(WorkspaceReadScope scope, int year, CancellationToken ct = default);

    /// <summary>Per-owner net distribution for <paramref name="year"/> (reuses the owner-statement math).</summary>
    Task<OwnerDistributionsResponse> GetOwnerDistributionsAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default);

    /// <summary>Work orders requested in the range, with status counts and a cost rollup.</summary>
    Task<WorkOrderReportResponse> GetWorkOrdersAsync(WorkspaceReadScope scope, ReportRangeQuery query, CancellationToken ct = default);
}
