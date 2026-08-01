using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Reports Hub: read-only reports over the caller's canonical workspace and property scope.
/// There are no create/update/delete operations. Property filters
/// (<c>propertyId</c> / <c>propertyIds</c>) are IDOR-validated against the portfolio inside the
/// service. Date ranges are <c>from</c>/<c>to</c> (inclusive) and default to year-to-date when omitted.
///
/// Reports already served by an existing engine — Schedule E (<c>/api/v1/accounting/schedule-e</c>),
/// Owner Statement (<c>/api/v1/accounting/owner-statement</c>), and the Year-End Packet
/// (<c>/api/v1/accounting/year-end-packet</c>) — are intentionally NOT re-implemented here. The
/// <c>GET /catalog</c> endpoint references those existing endpoints so the web hub can deep-link them.
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Produces("application/json")]
public class ReportsController : ManagementControllerBase
{
    private readonly IReportsService _service;

    public ReportsController(IReportsService service, TimeProvider timeProvider)
    {
        _service = service;
        _ = timeProvider;
    }

    /// <summary>
    /// The reports catalog: every available report grouped by category (Accounting / Rent &amp; Payments /
    /// Owners / Operations), each with a key, title, one-line description, the endpoint to call, and which
    /// params it accepts — so the web can render the report cards and the parameter bar generically.
    /// Includes the externally-served reports (Schedule E, Owner Statement, Year-End Packet) as deep links.
    /// </summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(ReportsCatalogResponse), StatusCodes.Status200OK)]
    public ActionResult<ReportsCatalogResponse> Catalog() => Ok(_service.GetCatalog());

    /// <summary>
    /// Rent Roll — current snapshot of every active/under-notice lease: property/unit, tenant, monthly
    /// rent, deposit, term, and status, plus portfolio totals. Optional <c>propertyId</c>/<c>propertyIds</c>.
    /// </summary>
    [HttpGet("rent-roll")]
    [ProducesResponseType(typeof(RentRollResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RentRollResponse>> RentRoll([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetRentRollAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Rent Ledger — per lease over <c>from</c>..<c>to</c>: charges due (accrual) vs. payments received,
    /// with a running balance per lease and portfolio totals. Range defaults to year-to-date.
    /// </summary>
    [HttpGet("rent-ledger")]
    [ProducesResponseType(typeof(RentLedgerResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RentLedgerResponse>> RentLedger([FromQuery] ReportRangeQuery query, CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access)) return Forbid();
        return Ok(await _service.GetRentLedgerAsync(access, query, ct));
    }

    /// <summary>
    /// Delinquency / Overdue Aging — outstanding balances bucketed by days past due
    /// (0-30 / 31-60 / 61-90 / 90+) per tenant/lease, plus bucket totals and a grand total.
    /// </summary>
    [HttpGet("delinquency")]
    [ProducesResponseType(typeof(DelinquencyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DelinquencyResponse>> Delinquency([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetDelinquencyAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Cash Flow — income vs. expense by month over <c>from</c>..<c>to</c>, with per-month net and grand
    /// totals. Doubles as the monthly-column P&amp;L / income-expense statement. Range defaults to YTD.
    /// </summary>
    [HttpGet("cash-flow")]
    [ProducesResponseType(typeof(CashFlowResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CashFlowResponse>> CashFlow([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetCashFlowAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// General Ledger / Account Transactions — every payment (income, positive) and expense (negative)
    /// over <c>from</c>..<c>to</c> in date order, with a cumulative running balance. Range defaults to YTD.
    /// </summary>
    [HttpGet("general-ledger")]
    [ProducesResponseType(typeof(GeneralLedgerResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GeneralLedgerResponse>> GeneralLedger([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetGeneralLedgerAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Property P&amp;L Summary — income / expense / net per property over <c>from</c>..<c>to</c>, plus
    /// portfolio totals. Range defaults to year-to-date.
    /// </summary>
    [HttpGet("property-pnl")]
    [ProducesResponseType(typeof(PropertyProfitAndLossResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PropertyProfitAndLossResponse>> PropertyPnl([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetPropertyProfitAndLossAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Occupancy / Vacancy — per property: total / occupied / vacant units and occupancy %, plus
    /// portfolio totals. Optional <c>propertyId</c>/<c>propertyIds</c>.
    /// </summary>
    [HttpGet("occupancy")]
    [ProducesResponseType(typeof(OccupancyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OccupancyResponse>> Occupancy([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetOccupancyAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Lease Expirations / Renewals Due — active/under-notice leases ending within the next
    /// <paramref name="days"/> days (default 90), ordered by end date, with totals.
    /// </summary>
    [HttpGet("lease-expirations")]
    [ProducesResponseType(typeof(LeaseExpirationsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LeaseExpirationsResponse>> LeaseExpirations(
        [FromQuery] ReportRangeQuery query, [FromQuery] int days = 90, CancellationToken ct = default)
        => Ok(await _service.GetLeaseExpirationsAsync(GetWorkspaceReadScope(), query, days, ct));

    /// <summary>
    /// Security Deposit Register — per lease: amount held, deductions, returned, and current balance still
    /// held in trust, plus portfolio totals. Optional <c>propertyId</c>/<c>propertyIds</c>.
    /// </summary>
    [HttpGet("security-deposits")]
    [ProducesResponseType(typeof(SecurityDepositRegisterResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SecurityDepositRegisterResponse>> SecurityDeposits([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetSecurityDepositRegisterAsync(GetWorkspaceReadScope(), query, ct));

    /// <summary>
    /// Vendor 1099 &amp; Payments — per 1099-eligible vendor (and any vendor paid in the year): total paid
    /// in <paramref name="year"/>, whether a W-9 is on file, and review flags (needs W-9, needs 1099).
    /// Defaults to the portfolio business year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("vendor-1099")]
    [ProducesResponseType(typeof(Vendor1099Response), StatusCodes.Status200OK)]
    public async Task<ActionResult<Vendor1099Response>> Vendor1099([FromQuery] int? year, CancellationToken ct)
    {
        var scope = GetWorkspaceReadScope();
        var reportYear = year ?? await _service.GetDefaultAnnualReportYearAsync(scope, ct);
        return Ok(await _service.GetVendor1099Async(scope, reportYear, ct));
    }

    /// <summary>
    /// Owner Distributions — net distribution per owner for <paramref name="year"/> (rental income minus
    /// expenses and management fee, matching the per-owner statement), with a portfolio total. Defaults to
    /// the portfolio business year when omitted.
    /// </summary>
    [HttpGet("owner-distributions")]
    [ProducesResponseType(typeof(OwnerDistributionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerDistributionsResponse>> OwnerDistributions([FromQuery] int? year, CancellationToken ct)
    {
        var scope = GetWorkspaceReadScope();
        var reportYear = year ?? await _service.GetDefaultAnnualReportYearAsync(scope, ct);
        return Ok(await _service.GetOwnerDistributionsAsync(scope, reportYear, ct));
    }

    /// <summary>
    /// Work Orders / Maintenance — work orders requested in <c>from</c>..<c>to</c>, with per-status counts
    /// (open vs. completed) and a recorded-cost rollup. Range defaults to year-to-date.
    /// </summary>
    [HttpGet("work-orders")]
    [ProducesResponseType(typeof(WorkOrderReportResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkOrderReportResponse>> WorkOrders([FromQuery] ReportRangeQuery query, CancellationToken ct)
        => Ok(await _service.GetWorkOrdersAsync(GetWorkspaceReadScope(), query, ct));
}
