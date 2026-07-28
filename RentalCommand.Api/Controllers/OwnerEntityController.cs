using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for owner entities (Person/LLC/Trust) within the caller's canonical workspace scope. The list
/// supports <c>?skip&amp;take&amp;search&amp;sort&amp;ownerEntityType</c>. Removal is a soft-delete.
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
    public async Task<ActionResult<IReadOnlyList<OwnerEntityResponse>>> List([FromQuery] OwnerEntityListQuery query, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var items = await _service.ListAsync(scope, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(OwnerEntityListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OwnerEntityListResponse>> ListPage([FromQuery] OwnerEntityListQuery query, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var page = await _service.ListPageAsync(scope, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerEntityResponse>> Get(int id, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var item = await _service.GetAsync(scope, id, ct);
        return item == null ? NotFound(new { error = "Owner entity not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<OwnerEntityResponse>> Create(
        [FromBody] CreateOwnerEntityRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var created = await _service.CreateAsync(scope, request, operationKey, ct);
        if (created == null) return NotFound(new { error = "Owner entity not found" });
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(OwnerEntityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerEntityResponse>> Update(
        int id,
        [FromBody] UpdateOwnerEntityRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAsync(scope, id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Owner entity not found" }) : Ok(updated);
    }

    [HttpPost("{id:int}/portal-access/activate")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    [ProducesResponseType(typeof(ActivateOwnerPortalAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ActivateOwnerPortalAccessResponse>> ActivatePortalAccess(
        int id,
        [FromBody] ActivateOwnerPortalAccessRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });

        try
        {
            var result = await _service.ActivateOwnerPortalAccessAsync(
                scope,
                id,
                request,
                operationKey,
                ct);
            return result.Outcome switch
            {
                ActivateOwnerPortalAccessOutcome.Activated or
                    ActivateOwnerPortalAccessOutcome.InvitationPending or
                    ActivateOwnerPortalAccessOutcome.AlreadyActive => Ok(result),
                ActivateOwnerPortalAccessOutcome.NotFound => NotFound(result),
                ActivateOwnerPortalAccessOutcome.Invalid or
                    ActivateOwnerPortalAccessOutcome.PrimaryOwnerNotSupported => BadRequest(result),
                _ => Conflict(result),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAsync(scope, id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Owner entity not found" });
    }
}
