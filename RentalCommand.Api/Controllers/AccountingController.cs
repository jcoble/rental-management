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

    public AccountingController(IAccountingService service)
    {
        _service = service;
    }

    /// <summary>Expense totals by Schedule E category plus collected/outstanding/overdue payment rollups.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AccountingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingSummaryResponse>> Summary(CancellationToken ct)
    {
        var summary = await _service.GetSummaryAsync(GetPortfolioId(), ct);
        return Ok(summary);
    }

    /// <summary>Ledger, property P&amp;L, Schedule E, and vendor 1099 review reports.</summary>
    [HttpGet("reports")]
    [ProducesResponseType(typeof(AccountingReportsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingReportsResponse>> Reports(CancellationToken ct)
    {
        var reports = await _service.GetReportsAsync(GetPortfolioId(), ct);
        return Ok(reports);
    }
}
