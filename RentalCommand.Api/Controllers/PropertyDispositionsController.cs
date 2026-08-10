using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/property-dispositions")]
[Produces("application/json")]
public class PropertyDispositionsController : ManagementControllerBase
{
    private readonly IPropertyDispositionService _service;

    public PropertyDispositionsController(IPropertyDispositionService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PropertyDispositionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PropertyDispositionResponse>>> List(
        [FromQuery] PropertyDispositionListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(PropertyDispositionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PropertyDispositionListResponse>> ListPage(
        [FromQuery] PropertyDispositionListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PropertyDispositionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyDispositionResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item is null ? NotFound(new { error = "Property disposition not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(PropertyDispositionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyDispositionResponse>> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreatePropertyDispositionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(new { error = "A request key is required." });
        if (idempotencyKey.Trim().Length > 200)
            return BadRequest(new { error = "A request key cannot exceed 200 characters." });
        var created = await _service.CreateAsync(
            GetActiveAccessContext(), request, idempotencyKey.Trim(), ct);
        return created is null
            ? NotFound(new { error = "Property not found in this portfolio or already has an active disposition" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(PropertyDispositionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyDispositionResponse>> Update(
        int id, [FromBody] UpdatePropertyDispositionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated is null ? NotFound(new { error = "Property disposition not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAuthorizedAsync(GetWorkspaceReadScope(), id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Property disposition not found" });
    }
}
