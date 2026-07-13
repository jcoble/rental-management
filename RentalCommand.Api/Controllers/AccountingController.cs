using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only accounting rollups for the caller's portfolio. Scope comes from the server-validated
/// workspace context. There are no create/update/delete operations here.
/// </summary>
[ApiController]
[Route("api/v1/accounting")]
[Produces("application/json")]
public class AccountingController : ManagementControllerBase
{
    private readonly IAccountingService _service;
    private readonly IScheduleEService _scheduleE;
    private readonly IOwnerStatementService _ownerStatements;
    private readonly IOwnerStatementEmailService _ownerStatementEmail;
    private readonly IReportsService _reports;
    private readonly TimeProvider _timeProvider;

    public AccountingController(
        IAccountingService service,
        IScheduleEService scheduleE,
        IOwnerStatementService ownerStatements,
        IOwnerStatementEmailService ownerStatementEmail,
        IReportsService reports,
        TimeProvider timeProvider)
    {
        _service = service;
        _scheduleE = scheduleE;
        _ownerStatements = ownerStatements;
        _ownerStatementEmail = ownerStatementEmail;
        _reports = reports;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// True cash flow per property + portfolio for the range (spec §9/§18): rent in − operating
    /// expenses (escrow-funded taxes/insurance excluded) − full debt service, with NOI and after-debt
    /// cash flow as distinct lines. Excludes deposits and non-cash depreciation. Defaults to the
    /// current year-to-date when the range is omitted.
    /// </summary>
    [HttpGet("cash-flow")]
    [ProducesResponseType(typeof(CashFlowSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CashFlowSummaryResponse>> CashFlow([FromQuery] ReportRangeQuery query, CancellationToken ct)
    {
        var result = await _reports.GetTrueCashFlowAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(result);
    }

    /// <summary>
    /// The year-end view (spec §11/§18): cash flow vs taxable income as distinct numbers, with
    /// depreciation + debt service present, the rent roll, and the "see your accountant" caveats.
    /// Defaults to the previous calendar year (the year you file for) when omitted.
    /// </summary>
    [HttpGet("year-end")]
    [ProducesResponseType(typeof(YearEndViewResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<YearEndViewResponse>> YearEnd(
        [FromQuery] int? year,
        [FromQuery] int? propertyId,
        CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year - 1;
        var result = await _reports.GetYearEndAsync(GetWorkspaceReadScope(), reportYear, propertyId, ct);
        return Ok(result);
    }

    /// <summary>Expense totals by Schedule E category plus collected/outstanding/overdue payment rollups.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AccountingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingSummaryResponse>> Summary(CancellationToken ct)
    {
        var summary = await _service.GetSummaryAsync(GetWorkspaceReadScope(), ct);
        return Ok(summary);
    }

    /// <summary>
    /// Plain-English money snapshot for the current month-to-date (plus trailing 30 days): money in,
    /// money out, what's kept, and how many tenants are behind — each with a one-sentence explanation a
    /// non-technical landlord can read at a glance. Feeds a dashboard card and the daily briefing.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(MoneySnapshotResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MoneySnapshotResponse>> Snapshot(CancellationToken ct)
    {
        var snapshot = await _service.GetSnapshotAsync(GetWorkspaceReadScope(), ct);
        return Ok(snapshot);
    }

    /// <summary>
    /// The "Who's behind" list: one actionable row per lease/tenant currently behind on rent. This is
    /// the destination behind the dashboard "tenants behind" KPI — both come from the same past-due
    /// definition, so the returned <c>TotalCount</c> always equals that KPI count.
    /// </summary>
    [HttpGet("past-due")]
    [ProducesResponseType(typeof(PastDueResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PastDueResponse>> PastDue(CancellationToken ct)
    {
        var pastDue = await _service.GetPastDueAsync(GetWorkspaceReadScope(), ct);
        return Ok(pastDue);
    }

    /// <summary>Ledger, property P&amp;L, Schedule E, and vendor 1099 review reports.</summary>
    [HttpGet("reports")]
    [ProducesResponseType(typeof(AccountingReportsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingReportsResponse>> Reports(CancellationToken ct)
    {
        var reports = await _service.GetReportsAsync(GetWorkspaceReadScope(), ct);
        return Ok(reports);
    }

    /// <summary>Unified, paginated payment and expense transactions for the accounting workspace.</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(AccountingTransactionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingTransactionsResponse>> Transactions(
        [FromQuery] AccountingTransactionsQuery query,
        CancellationToken ct)
    {
        var transactions = await _service.GetTransactionsAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(transactions);
    }

    /// <summary>
    /// Year-end Schedule E report: per-property rental income and deductible expenses grouped by
    /// IRS Schedule E category. Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("schedule-e")]
    [ProducesResponseType(typeof(ScheduleEReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<ScheduleEReport>> ScheduleE(
        [FromQuery] int? year,
        [FromQuery] int? propertyId,
        CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var report = await _scheduleE.GetReportAsync(GetWorkspaceReadScope(), reportYear, propertyId, ct);
        return Ok(report);
    }

    /// <summary>
    /// Downloads the Schedule E report as a CSV file. Each property has a rental-income row,
    /// one row per non-zero expense category, and a net-income row. A grand-total block appears at
    /// the end. Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("schedule-e/export")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.DataExport)]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ScheduleEExport([FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var report = await _scheduleE.GetReportAsync(GetWorkspaceReadScope(), reportYear, ct: ct);

        var csv = BuildCsv(report);
        var bytes = Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv", $"schedule-e-{reportYear}.csv");
    }

    /// <summary>
    /// Downloads the year-end accountant packet as a single PDF: cover page, Schedule E summary,
    /// per-property profit &amp; loss, a month-by-month cash-flow summary, and a rent roll. This is the
    /// "clean books" document a landlord hands their accountant. Defaults to the previous calendar year
    /// when <paramref name="year"/> is omitted (that's the year you file for).
    /// </summary>
    [HttpGet("year-end-packet")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.DataExport)]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> YearEndPacket([FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year - 1;
        var pdf = await _service.GetYearEndPacketAsync(GetWorkspaceReadScope(), reportYear, ct);
        // Inline so it previews in the browser; the filename still applies on download/save.
        return File(pdf, "application/pdf", $"year-end-{reportYear}.pdf");
    }

    // ── Owner Statement endpoints ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all owners that have at least one property in the portfolio, with their net distribution
    /// for <paramref name="year"/>. Defaults to the current UTC year when omitted.
    /// </summary>
    [HttpGet("owner-statements")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerStatementSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OwnerStatementSummary>>> OwnerStatements(
        [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var summaries = await _ownerStatements.ListOwnersWithNetAsync(GetWorkspaceReadScope(), reportYear, ct);
        return Ok(summaries);
    }

    /// <summary>
    /// Full owner statement for a single owner: per-property income, expenses, management fee, and
    /// net distribution, plus portfolio-level totals. Defaults to the current UTC year when omitted.
    /// Returns 404 if <paramref name="ownerId"/> is not found in the portfolio.
    /// </summary>
    [HttpGet("owner-statement")]
    [ProducesResponseType(typeof(OwnerStatementReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerStatementReport>> OwnerStatement(
        [FromQuery] int ownerId, [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var report = await _ownerStatements.GetForOwnerAsync(GetWorkspaceReadScope(), ownerId, reportYear, ct);
        if (report is null)
            return NotFound();
        return Ok(report);
    }

    /// <summary>
    /// Downloads the owner statement as a CSV. Columns: Property, Income, Expenses, ManagementFee,
    /// NetToOwner. Total, distributed, and undistributed rows are appended at the end. Returns 404 if
    /// the owner is not found.
    /// Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("owner-statement/export")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.DataExport)]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OwnerStatementExport(
        [FromQuery] int ownerId, [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var report = await _ownerStatements.GetForOwnerAsync(GetWorkspaceReadScope(), ownerId, reportYear, ct);
        if (report is null)
            return NotFound();

        var csv = BuildOwnerStatementCsv(report);
        var bytes = Encoding.UTF8.GetBytes(csv);
        return File(bytes, "text/csv", $"owner-statement-{ownerId}-{reportYear}.csv");
    }

    // ── Owner Statement email endpoint ──────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues an on-demand owner-statement email for <paramref name="ownerId"/>.
    /// The owner must have an <c>Email</c> address set. Returns 400 with an error message when
    /// the owner has no email or no statement data exists for the requested year.
    /// Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpPost("owner-statements/{ownerId:int}/email")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.DataExport)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EmailOwnerStatement(int ownerId, [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? _timeProvider.UtcNow().Year;
        var result = await _ownerStatementEmail.SendOwnerStatementAsync(
            GetWorkspaceReadScope(), ownerId, reportYear, ct);
        if (!result.Sent)
            return BadRequest(new { error = result.Reason });
        return Ok(new { queued = true });
    }

    // ── CSV helpers ─────────────────────────────────────────────────────────────────────────────

    private static string BuildOwnerStatementCsv(OwnerStatementReport report)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Property,Income,Expenses,ManagementFee,NetToOwner");

        foreach (var line in report.Properties)
        {
            sb.AppendLine(
                $"{CsvField(line.PropertyName)}," +
                $"{CsvField(line.RentalIncome.ToString("F2"))}," +
                $"{CsvField(line.Expenses.ToString("F2"))}," +
                $"{CsvField(line.ManagementFee.ToString("F2"))}," +
                $"{CsvField(line.NetToOwner.ToString("F2"))}");
        }

        // TOTAL / distribution rows
        sb.AppendLine(
            $"{CsvField("TOTAL")}," +
            $"{CsvField(report.TotalIncome.ToString("F2"))}," +
            $"{CsvField(report.TotalExpenses.ToString("F2"))}," +
            $"{CsvField(report.TotalManagementFee.ToString("F2"))}," +
            $"{CsvField(report.TotalNetToOwner.ToString("F2"))}");
        sb.AppendLine(
            $"{CsvField("DISTRIBUTED")},,,," +
            $"{CsvField(report.TotalDistributed.ToString("F2"))}");
        sb.AppendLine(
            $"{CsvField("UNDISTRIBUTED")},,,," +
            $"{CsvField(report.Undistributed.ToString("F2"))}");

        return sb.ToString();
    }

    private static string BuildCsv(ScheduleEReport report)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Property,Category,Amount");

        foreach (var prop in report.Properties)
        {
            var name = CsvField(prop.PropertyName);

            // Rental income row
            sb.AppendLine($"{name},Rental Income,{CsvField(prop.RentalIncome.ToString("F2"))}");

            // One row per expense category
            foreach (var cat in prop.ExpensesByCategory)
            {
                sb.AppendLine($"{name},{CsvField(cat.Category)},{CsvField(cat.Amount.ToString("F2"))}");
            }

            // Net row (may be negative — guarded against a leading '-')
            sb.AppendLine($"{name},Net Income,{CsvField(prop.NetIncome.ToString("F2"))}");

            // Blank separator between properties
            sb.AppendLine();
        }

        // Grand totals block
        sb.AppendLine($"{CsvField("TOTAL")},Rental Income,{CsvField(report.TotalRentalIncome.ToString("F2"))}");
        sb.AppendLine($"{CsvField("TOTAL")},Total Expenses,{CsvField(report.TotalExpenses.ToString("F2"))}");
        sb.AppendLine($"{CsvField("TOTAL")},Net Income,{CsvField(report.NetIncome.ToString("F2"))}");

        if (report.UnallocatedActivity.RequiresAllocation)
        {
            sb.AppendLine();
            sb.AppendLine($"{CsvField("NEEDS ALLOCATION")},Income entries,{report.UnallocatedActivity.IncomeEntryCount}");
            sb.AppendLine($"{CsvField("NEEDS ALLOCATION")},Rental Income,{CsvField(report.UnallocatedActivity.RentalIncome.ToString("F2"))}");
            foreach (var category in report.UnallocatedActivity.ExpensesByCategory)
            {
                sb.AppendLine($"{CsvField("NEEDS ALLOCATION")},{CsvField(category.Category)},{CsvField(category.Amount.ToString("F2"))}");
            }
            sb.AppendLine($"{CsvField("NEEDS ALLOCATION")},Expense count,{report.UnallocatedActivity.ExpenseCount}");
            sb.AppendLine($"{CsvField("RECONCILED")},Rental Income,{CsvField(report.ReconciledTotalRentalIncome.ToString("F2"))}");
            sb.AppendLine($"{CsvField("RECONCILED")},Total Expenses,{CsvField(report.ReconciledTotalExpenses.ToString("F2"))}");
            sb.AppendLine($"{CsvField("RECONCILED")},Net Income,{CsvField(report.ReconciledNetIncome.ToString("F2"))}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes a CSV field when needed and guards against spreadsheet formula injection: values that
    /// begin with = + - @ (or a tab/CR) are prefixed with a single quote so Excel/Sheets treats them
    /// as text rather than executing them. Property names are user-controlled, so this matters.
    /// </summary>
    private static string CsvField(string? value)
    {
        value ??= string.Empty;
        var needsFormulaGuard = value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0;
        if (needsFormulaGuard)
        {
            value = "'" + value;
        }

        if (needsFormulaGuard ||
            value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
