using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for per-lease opening balances within the caller's portfolio — the balance a tenant already
/// carried when the landlord migrated onto Rental Command, so the books and the transparent ledger don't
/// drop pre-app history. Scope comes from the JWT <c>portfolioId</c> claim; list supports an optional
/// <c>?leaseId</c> filter. Create validates the lease is in the portfolio and rejects a duplicate (one
/// opening balance per lease — PATCH the existing one instead). Removal is a hard delete.
/// </summary>
[ApiController]
[Route("api/v1/opening-balances")]
[Produces("application/json")]
public class OpeningBalanceController : ManagementControllerBase
{
    private readonly IOpeningBalanceService _service;

    public OpeningBalanceController(IOpeningBalanceService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OpeningBalanceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OpeningBalanceResponse>>> List(
        [FromQuery] int? leaseId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), leaseId, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OpeningBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpeningBalanceResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Opening balance not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(OpeningBalanceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OpeningBalanceResponse>> Create([FromBody] CreateOpeningBalanceRequest request, CancellationToken ct)
    {
        var (result, response) = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return result switch
        {
            CreateOpeningBalanceResult.LeaseNotFound =>
                NotFound(new { error = "Lease not found in this portfolio" }),
            CreateOpeningBalanceResult.AlreadyExists =>
                Conflict(new { error = "An opening balance already exists for this lease; update it instead." }),
            _ => CreatedAtAction(nameof(Get), new { id = response!.Id }, response),
        };
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(OpeningBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OpeningBalanceResponse>> Update(int id, [FromBody] UpdateOpeningBalanceRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Opening balance not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Opening balance not found" });
    }
}
