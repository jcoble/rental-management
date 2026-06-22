using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for owner entities (Person/LLC/Trust) within the caller's portfolio. Scope comes from the JWT
/// <c>portfolioId</c> claim; list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/owner-entities")]
[Produces("application/json")]
public class OwnerEntityController : ManagementControllerBase
{
    private readonly IOwnerEntityService _service;

    public OwnerEntityController(IOwnerEntityService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerEntityResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OwnerEntityResponse>>> List([FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(OwnerEntityListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerEntityListResponse>> ListPage([FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerEntityResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Owner entity not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<OwnerEntityResponse>> Create([FromBody] CreateOwnerEntityRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerEntityResponse>> Update(int id, [FromBody] UpdateOwnerEntityRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Owner entity not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Owner entity not found" });
    }
}
