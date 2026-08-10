using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for standing recurring-maintenance chores within the caller's portfolio (HVAC filter every
/// 90 days, quarterly gutter cleaning, …). The Engine's recurring-maintenance worker turns each due
/// task into a <see cref="Core.Entities.WorkOrder"/> per period. Scope comes from the canonical
/// workspace and property access context; the list supports <c>?propertyId&amp;activeOnly&amp;skip&amp;take&amp;search&amp;sort</c>.
/// Create/update validate the referenced property/unit/vendor are in the portfolio. Delete is a soft
/// delete; <c>PATCH {id}/active</c> flips the per-task on/off switch.
/// </summary>
[ApiController]
[Route("api/v1/recurring-maintenance")]
[Produces("application/json")]
public class RecurringMaintenanceController : ManagementControllerBase
{
    private readonly IRecurringMaintenanceTaskService _service;

    public RecurringMaintenanceController(IRecurringMaintenanceTaskService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RecurringMaintenanceTaskResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RecurringMaintenanceTaskResponse>>> List(
        [FromQuery] RecurringMaintenanceTaskListQuery query, [FromQuery] int? propertyId, [FromQuery] bool? activeOnly, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(
            GetWorkspaceReadScope(), propertyId, activeOnly, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(RecurringMaintenanceTaskListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RecurringMaintenanceTaskListResponse>> ListPage(
        [FromQuery] RecurringMaintenanceTaskListQuery query, [FromQuery] int? propertyId, [FromQuery] bool? activeOnly, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(
            GetWorkspaceReadScope(), propertyId, activeOnly, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(RecurringMaintenanceTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringMaintenanceTaskResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Recurring maintenance task not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(RecurringMaintenanceTaskResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringMaintenanceTaskResponse>> Create(
        [FromBody] CreateRecurringMaintenanceTaskRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var created = await _service.CreateAuthorizedAsync(
            GetWorkspaceReadScope(), request, operationKey, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, or vendor not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(RecurringMaintenanceTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringMaintenanceTaskResponse>> Update(
        int id,
        [FromBody] UpdateRecurringMaintenanceTaskRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated == null
            ? NotFound(new { error = "Recurring maintenance task not found, or referenced unit/vendor not in this portfolio" })
            : Ok(updated);
    }

    [HttpPatch("{id:int}/active")]
    [ProducesResponseType(typeof(RecurringMaintenanceTaskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringMaintenanceTaskResponse>> ToggleActive(
        int id,
        [FromBody] ToggleRecurringMaintenanceTaskActiveRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var updated = await _service.SetActiveAuthorizedAsync(
            GetWorkspaceReadScope(), id, request.IsActive, operationKey, ct);
        return updated == null ? NotFound(new { error = "Recurring maintenance task not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAuthorizedAsync(
            GetWorkspaceReadScope(), id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Recurring maintenance task not found" });
    }
}
