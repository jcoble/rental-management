using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Manages security deposit holdings through their full lifecycle (hold → deductions → return)
/// within the caller's portfolio.
/// </summary>
[ApiController]
[Route("api/v1/security-deposits")]
[Produces("application/json")]
public class SecurityDepositsController : AuthenticatedPortfolioControllerBase
{
    private readonly ISecurityDepositService _service;

    public SecurityDepositsController(ISecurityDepositService service)
    {
        _service = service;
    }

    /// <summary>List all deposit holdings for the portfolio, optionally filtered by lease.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SecurityDepositResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SecurityDepositResponse>>> List(
        [FromQuery] int? leaseId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), leaseId, ct);
        return Ok(items);
    }

    /// <summary>Get a single deposit holding by id.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(SecurityDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SecurityDepositResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Security deposit holding not found" }) : Ok(item);
    }

    /// <summary>
    /// Create a new deposit holding for a lease. Amount defaults to the lease's SecurityDeposit
    /// when not supplied in the request.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SecurityDepositResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SecurityDepositResponse>> Create(
        [FromBody] CreateDepositRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        if (created == null)
            return BadRequest(new { error = "Lease not found in this portfolio" });

        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Append an itemised deduction to an existing holding.</summary>
    [HttpPost("{id:int}/deductions")]
    [ProducesResponseType(typeof(SecurityDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SecurityDepositResponse>> AddDeduction(
        int id, [FromBody] AddDeductionRequest request, CancellationToken ct)
    {
        var updated = await _service.AddDeductionAsync(GetPortfolioId(), id, request, ct);
        return updated == null
            ? NotFound(new { error = "Security deposit holding not found or already returned" })
            : Ok(updated);
    }

    /// <summary>
    /// Finalise the return: compute net refund from Amount minus all deductions, set ReturnedAt,
    /// and transition Status to Returned or PartiallyReturned.
    /// </summary>
    [HttpPost("{id:int}/return")]
    [ProducesResponseType(typeof(SecurityDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SecurityDepositResponse>> Return(
        int id, [FromBody] ReturnDepositRequest request, CancellationToken ct)
    {
        var updated = await _service.ReturnAsync(GetPortfolioId(), id, request, ct);
        return updated == null
            ? NotFound(new { error = "Security deposit holding not found or already returned" })
            : Ok(updated);
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
