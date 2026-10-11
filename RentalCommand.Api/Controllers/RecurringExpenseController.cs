using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for recurring-expense templates (insurance/tax/HOA/management fee entered once) within the
/// caller's portfolio. Scope comes from the server-validated workspace context; list supports
/// <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort&amp;from&amp;to</c>. Removal is a soft-delete. The Engine
/// materializes due templates into expense rows.
/// </summary>
[ApiController]
[Route("api/v1/recurring-expenses")]
[Produces("application/json")]
public class RecurringExpenseController : ManagementControllerBase
{
    private readonly IRecurringExpenseService _service;

    public RecurringExpenseController(IRecurringExpenseService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RecurringExpenseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RecurringExpenseResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetWorkspaceReadScope(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(RecurringExpenseListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RecurringExpenseListResponse>> ListPage(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetWorkspaceReadScope(), propertyId, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(RecurringExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringExpenseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Recurring expense not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(RecurringExpenseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringExpenseResponse>> Create(
        [FromBody] CreateRecurringExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        try
        {
            var created = await _service.CreateAsync(GetWorkspaceReadScope(), request, operationKey, ct);
            return created == null
                ? NotFound(new { error = "Referenced property or unit not found in this portfolio" })
                : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(RecurringExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringExpenseResponse>> Update(
        int id, [FromBody] UpdateRecurringExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        try
        {
            var updated = await _service.UpdateAsync(GetWorkspaceReadScope(), id, request, operationKey, ct);
            return updated == null ? NotFound(new { error = "Recurring expense not found" }) : Ok(updated);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        try
        {
            var deleted = await _service.DeleteAsync(GetWorkspaceReadScope(), id, operationKey, ct);
            return deleted ? NoContent() : NotFound(new { error = "Recurring expense not found" });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }
}
