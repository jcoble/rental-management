using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for owner distributions: cash paid to owners from net proceeds. These are never expenses.
/// </summary>
[ApiController]
[Route("api/v1/owner-distributions")]
[Produces("application/json")]
public class OwnerDistributionController : AuthenticatedPortfolioControllerBase
{
    private readonly IOwnerDistributionService _service;

    public OwnerDistributionController(IOwnerDistributionService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerDistributionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OwnerDistributionResponse>>> List(
        [FromQuery] OwnerDistributionListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(OwnerDistributionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerDistributionListResponse>> ListPage(
        [FromQuery] OwnerDistributionListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item is null ? NotFound(new { error = "Owner distribution not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Create(
        [FromBody] CreateOwnerDistributionRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created is null
            ? NotFound(new { error = "Referenced owner or property not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Update(
        int id, [FromBody] UpdateOwnerDistributionRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated is null ? NotFound(new { error = "Owner distribution not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Owner distribution not found" });
    }
}
