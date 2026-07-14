using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for owner distributions: cash paid to owners from net proceeds. These are never expenses.
/// </summary>
[ApiController]
[Route("api/v1/owner-distributions")]
[Produces("application/json")]
public class OwnerDistributionController : ManagementControllerBase
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
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var items = await _service.ListAsync(scope, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(OwnerDistributionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerDistributionListResponse>> ListPage(
        [FromQuery] OwnerDistributionListQuery query, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var page = await _service.ListPageAsync(scope, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Get(int id, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var item = await _service.GetAsync(scope, id, ct);
        return item is null ? NotFound(new { error = "Owner distribution not found" }) : Ok(item);
    }

    [HttpPost]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Create(
        [FromBody] CreateOwnerDistributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var created = await _service.CreateAsync(scope, request, operationKey, ct);
            return created is null
                ? NotFound(new { error = "Referenced owner or property not found in this portfolio" })
                : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpPatch("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerDistributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerDistributionResponse>> Update(
        int id, [FromBody] UpdateOwnerDistributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var updated = await _service.UpdateAsync(scope, id, request, operationKey, ct);
            return updated is null ? NotFound(new { error = "Owner distribution not found" }) : Ok(updated);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var deleted = await _service.DeleteAsync(scope, id, operationKey, ct);
            return deleted ? NoContent() : NotFound(new { error = "Owner distribution not found" });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }
}
