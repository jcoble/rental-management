using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only accounting rollups for the caller's portfolio. Scope comes from the JWT <c>portfolioId</c>
/// claim. There are no create/update/delete operations here.
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

    public AccountingController(
        IAccountingService service,
        IScheduleEService scheduleE,
        IOwnerStatementService ownerStatements,
        IOwnerStatementEmailService ownerStatementEmail)
    {
        _service = service;
        _scheduleE = scheduleE;
        _ownerStatements = ownerStatements;
        _ownerStatementEmail = ownerStatementEmail;
    }

    /// <summary>Expense totals by Schedule E category plus collected/outstanding/overdue payment rollups.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AccountingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingSummaryResponse>> Summary(CancellationToken ct)
    {
        var summary = await _service.GetSummaryAsync(GetPortfolioId(), ct);
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
        var snapshot = await _service.GetSnapshotAsync(GetPortfolioId(), ct);
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
        var pastDue = await _service.GetPastDueAsync(GetPortfolioId(), ct);
        return Ok(pastDue);
    }

    /// <summary>Ledger, property P&amp;L, Schedule E, and vendor 1099 review reports.</summary>
    [HttpGet("reports")]
    [ProducesResponseType(typeof(AccountingReportsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingReportsResponse>> Reports(CancellationToken ct)
    {
        var reports = await _service.GetReportsAsync(GetPortfolioId(), ct);
        return Ok(reports);
    }

    /// <summary>Unified, paginated payment and expense transactions for the accounting workspace.</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(AccountingTransactionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingTransactionsResponse>> Transactions(
        [FromQuery] AccountingTransactionsQuery query,
        CancellationToken ct)
    {
        var transactions = await _service.GetTransactionsAsync(GetPortfolioId(), query, ct);
        return Ok(transactions);
    }

    /// <summary>
    /// Year-end Schedule E report: per-property rental income and deductible expenses grouped by
    /// IRS Schedule E category. Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("schedule-e")]
    [ProducesResponseType(typeof(ScheduleEReport), StatusCodes.Status200OK)]
    public async Task<ActionResult<ScheduleEReport>> ScheduleE([FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? DateTime.UtcNow.Year;
        var report = await _scheduleE.GetReportAsync(GetPortfolioId(), reportYear, ct);
        return Ok(report);
    }

    /// <summary>
    /// Downloads the Schedule E report as a CSV file. Each property has a rental-income row,
    /// one row per non-zero expense category, and a net-income row. A grand-total block appears at
    /// the end. Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("schedule-e/export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ScheduleEExport([FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? DateTime.UtcNow.Year;
        var report = await _scheduleE.GetReportAsync(GetPortfolioId(), reportYear, ct);

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
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> YearEndPacket([FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? DateTime.UtcNow.Year - 1;
        var pdf = await _service.GetYearEndPacketAsync(GetPortfolioId(), reportYear, ct);
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
        var reportYear = year ?? DateTime.UtcNow.Year;
        var summaries = await _ownerStatements.ListOwnersWithNetAsync(GetPortfolioId(), reportYear, ct);
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
        var reportYear = year ?? DateTime.UtcNow.Year;
        var report = await _ownerStatements.GetForOwnerAsync(GetPortfolioId(), ownerId, reportYear, ct);
        if (report is null)
            return NotFound();
        return Ok(report);
    }

    /// <summary>
    /// Downloads the owner statement as a CSV. Columns: Property, Income, Expenses, ManagementFee,
    /// NetToOwner. A TOTAL row is appended at the end. Returns 404 if the owner is not found.
    /// Defaults to the current UTC year when <paramref name="year"/> is omitted.
    /// </summary>
    [HttpGet("owner-statement/export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OwnerStatementExport(
        [FromQuery] int ownerId, [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? DateTime.UtcNow.Year;
        var report = await _ownerStatements.GetForOwnerAsync(GetPortfolioId(), ownerId, reportYear, ct);
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
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EmailOwnerStatement(int ownerId, [FromQuery] int? year, CancellationToken ct)
    {
        var reportYear = year ?? DateTime.UtcNow.Year;
        var result = await _ownerStatementEmail.SendOwnerStatementAsync(GetPortfolioId(), ownerId, reportYear, ct);
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

        // TOTAL row
        sb.AppendLine(
            $"{CsvField("TOTAL")}," +
            $"{CsvField(report.TotalIncome.ToString("F2"))}," +
            $"{CsvField(report.TotalExpenses.ToString("F2"))}," +
            $"{CsvField(report.TotalManagementFee.ToString("F2"))}," +
            $"{CsvField(report.TotalNetToOwner.ToString("F2"))}");

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
