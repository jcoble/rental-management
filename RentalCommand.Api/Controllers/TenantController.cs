using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for tenants within the caller's portfolio. Scope comes from the server-validated workspace context;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Produces("application/json")]
public class TenantController : ManagementControllerBase
{
    private readonly ITenantService _service;

    public TenantController(ITenantService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List([FromQuery] TenantListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(TenantListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantListResponse>> ListPage([FromQuery] TenantListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Tenant not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<TenantResponse>> Create(
        [FromBody] CreateTenantRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var created = await _service.CreateAuthorizedAsync(GetWorkspaceReadScope(), request, operationKey, ct);
        if (created is null)
        {
            return Forbid();
        }

        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>
    /// Creates every reviewed Guided Setup tenant in one receipt-backed database transaction.
    /// A failed row rolls back the complete batch; retrying the same key returns the same tenants.
    /// </summary>
    [HttpPost("guided-setup")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> CreateGuidedSetupBatch(
        [FromBody] GuidedTenantSetupRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });

        var created = await _service.CreateGuidedSetupBatchAsync(
            GetWorkspaceReadScope(), request, operationKey, ct);
        return Ok(created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Update(
        int id,
        [FromBody] UpdateTenantRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAuthorizedAsync(GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Tenant not found" }) : Ok(updated);
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
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAuthorizedAsync(GetWorkspaceReadScope(), id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Tenant not found" });
    }

    // Resident login grants are deliberately absent here. They are relationship-scoped and are
    // created/revoked only by LeaseManagementController's atomic party-access commands.
}
