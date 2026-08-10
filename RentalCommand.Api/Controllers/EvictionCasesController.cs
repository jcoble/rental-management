using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/eviction-cases")]
[Produces("application/json")]
public class EvictionCasesController : ManagementControllerBase
{
    private readonly IEvictionCaseService _service;

    public EvictionCasesController(IEvictionCaseService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<EvictionCaseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EvictionCaseResponse>>> List(
        [FromQuery] EvictionCaseListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(EvictionCaseListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EvictionCaseListResponse>> ListPage(
        [FromQuery] EvictionCaseListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(EvictionCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvictionCaseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item is null ? NotFound(new { error = "Eviction case not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(EvictionCaseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvictionCaseResponse>> Create(
        [FromBody] CreateEvictionCaseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var created = await _service.CreateAuthorizedAsync(
            GetWorkspaceReadScope(), request, operationKey, ct);
        return created is null
            ? NotFound(new { error = "Lease relationship, agreement, or respondent party not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(EvictionCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvictionCaseResponse>> Update(
        int id, [FromBody] UpdateEvictionCaseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated is null ? NotFound(new { error = "Eviction case not found" }) : Ok(updated);
    }

    [HttpPost("{id:int}/events")]
    [ProducesResponseType(typeof(EvictionCaseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EvictionCaseResponse>> AddEvent(
        int id, [FromBody] CreateEvictionCaseEventRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var updated = await _service.AddEventAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated is null
            ? NotFound(new { error = "Eviction case not found" })
            : CreatedAtAction(nameof(Get), new { id = updated.Id }, updated);
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
        return deleted ? NoContent() : NotFound(new { error = "Eviction case not found" });
    }
}
