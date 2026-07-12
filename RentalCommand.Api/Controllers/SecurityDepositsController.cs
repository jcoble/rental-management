using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Reads canonical security-deposit account projections. Fund, deduction, and refund facts are
/// append-only commands on <see cref="TenantAccountMoneyController"/>.
/// </summary>
[ApiController]
[Route("api/v1/security-deposits")]
[Produces("application/json")]
public class SecurityDepositsController : ManagementControllerBase
{
    private readonly ISecurityDepositService _service;

    public SecurityDepositsController(ISecurityDepositService service)
    {
        _service = service;
    }

    /// <summary>List all deposit holdings for the portfolio, optionally filtered by lease.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SecurityDepositAccountResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SecurityDepositAccountResponse>>> List(
        [FromQuery] int? leaseManagementId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), leaseManagementId, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(SecurityDepositListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SecurityDepositListResponse>> ListPage(
        [FromQuery] int? leaseManagementId, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), leaseManagementId, query, ct);
        return Ok(page);
    }

    /// <summary>Get a single deposit holding by id.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(SecurityDepositAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SecurityDepositAccountResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Security deposit holding not found" }) : Ok(item);
    }

    /// <summary>
    /// Downloads an itemised, photo-backed move-out statement PDF for the holding: deposit held, each
    /// deduction (reason + amount), any photos attached to the deposit, and the net refund (or amount
    /// owed). This is the dispute-proof document handed to the departing tenant. 404 if not in portfolio.
    /// </summary>
    [HttpGet("{id:int}/move-out-statement")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveOutStatement(int id, CancellationToken ct)
    {
        var pdf = await _service.GetMoveOutStatementAsync(GetPortfolioId(), id, ct);
        if (pdf == null)
            return NotFound(new { error = "Security deposit holding not found" });

        // Inline so it previews in the browser; the filename still applies on download/save.
        return File(pdf, "application/pdf", $"move-out-statement-{id}.pdf");
    }
}
