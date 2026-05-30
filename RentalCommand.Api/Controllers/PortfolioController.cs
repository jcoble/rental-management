using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Portfolio access for the signed-in user. Scope comes from the JWT <c>portfolioId</c> claim — the user
/// sees and edits only their own portfolio. Creating a portfolio scopes the current user to it.
/// </summary>
[ApiController]
[Route("api/v1/portfolios")]
[Produces("application/json")]
public class PortfolioController : AuthenticatedPortfolioControllerBase
{
    private readonly IPortfolioService _service;

    public PortfolioController(IPortfolioService service)
    {
        _service = service;
    }

    /// <summary>List the caller's portfolio(s) (their claim-scoped portfolio in Phase 0).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PortfolioResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PortfolioResponse>>> List(CancellationToken ct)
    {
        var items = await _service.ListForUserAsync(GetPortfolioId(), ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortfolioResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Portfolio not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<PortfolioResponse>> Create([FromBody] CreatePortfolioRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetUserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortfolioResponse>> Update(int id, [FromBody] UpdatePortfolioRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Portfolio not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Portfolio not found" });
    }
}
