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
public class AccountingController : AuthenticatedPortfolioControllerBase
{
    private readonly IAccountingService _service;
    private readonly IScheduleEService _scheduleE;

    public AccountingController(IAccountingService service, IScheduleEService scheduleE)
    {
        _service = service;
        _scheduleE = scheduleE;
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

    // ── CSV helpers ─────────────────────────────────────────────────────────────────────────────

    private static string BuildCsv(ScheduleEReport report)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine("Property,Category,Amount");

        foreach (var prop in report.Properties)
        {
            var name = CsvField(prop.PropertyName);

            // Rental income row
            sb.AppendLine($"{name},Rental Income,{prop.RentalIncome:F2}");

            // One row per expense category
            foreach (var cat in prop.ExpensesByCategory)
            {
                sb.AppendLine($"{name},{CsvField(cat.Category)},{cat.Amount:F2}");
            }

            // Net row
            sb.AppendLine($"{name},Net Income,{prop.NetIncome:F2}");

            // Blank separator between properties
            sb.AppendLine();
        }

        // Grand totals block
        sb.AppendLine($"{CsvField("TOTAL")},Rental Income,{report.TotalRentalIncome:F2}");
        sb.AppendLine($"{CsvField("TOTAL")},Total Expenses,{report.TotalExpenses:F2}");
        sb.AppendLine($"{CsvField("TOTAL")},Net Income,{report.NetIncome:F2}");

        return sb.ToString();
    }

    /// <summary>Quotes a CSV field if it contains a comma, double-quote, or newline.</summary>
    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
