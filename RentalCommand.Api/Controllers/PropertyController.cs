using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for properties within the caller's portfolio. Scope comes from the server-validated workspace context;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/properties")]
[Produces("application/json")]
public class PropertyController : ManagementControllerBase
{
    private readonly IPropertyService _service;

    public PropertyController(IPropertyService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PropertyResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PropertyResponse>>> List([FromQuery] PropertyListQuery query, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var items = await _service.ListAsync(scope, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(PropertyListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PropertyListResponse>> ListPage([FromQuery] PropertyListQuery query, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var result = await _service.ListPageAsync(scope, query, ct);
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyResponse>> Get(int id, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var item = await _service.GetAsync(scope, id, ct);
        return item == null ? NotFound(new { error = "Property not found" }) : Ok(item);
    }

    /// <summary>
    /// Creates or updates one Property and explicitly supplied Units in one receipt-backed database
    /// transaction. No implicit Unit is created from the Property type or RentalStructure.
    /// </summary>
    [HttpPost("setup")]
    [ProducesResponseType(typeof(PropertySetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PropertySetupResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<PropertySetupResponse>> Setup(
        [FromBody] SetupPropertyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });

        var result = await _service.SetupAsync(scope, request, operationKey, ct);
        if (result is null)
            return NotFound(new { error = "Property or OwnerEntity not found in this workspace" });
        return result.Updated
            ? Ok(result)
            : CreatedAtAction(nameof(Get), new { id = result.Property.Id }, result);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(PropertyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyResponse>> Update(
        int id,
        [FromBody] UpdatePropertyRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAsync(scope, id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Property not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAsync(scope, id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Property not found" });
    }
}
