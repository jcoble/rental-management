using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for vendors within the caller's portfolio. Scope comes from the server-validated workspace context;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/vendors")]
[Produces("application/json")]
public class VendorController : ManagementControllerBase
{
    private readonly IVendorService _service;
    private readonly IVendorDispatchService _dispatch;

    public VendorController(IVendorService service, IVendorDispatchService dispatch)
    {
        _service = service;
        _dispatch = dispatch;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<VendorResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<VendorResponse>>> List([FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var items = await _service.ListAsync(scope, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(VendorListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<VendorListResponse>> ListPage([FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var page = await _service.ListPageAsync(scope, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorResponse>> Get(int id, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var item = await _service.GetAsync(scope, id, ct);
        return item == null ? NotFound(new { error = "Vendor not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<VendorResponse>> Create(
        [FromBody] CreateVendorRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var created = await _service.CreateAsync(scope, request, operationKey, ct);
        if (created == null) return NotFound(new { error = "Vendor not found" });
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorResponse>> Update(
        int id,
        [FromBody] UpdateVendorRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var updated = await _service.UpdateAsync(scope, id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Vendor not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key header is required and cannot exceed 128 characters." });
        var deleted = await _service.DeleteAsync(scope, id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Vendor not found" });
    }

    /// <summary>Record a 1–5 star rating for the vendor and refresh its cached scorecard aggregates.</summary>
    [HttpPost("{id:int}/ratings")]
    [ProducesResponseType(typeof(VendorRatingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorRatingResponse>> Rate(int id, [FromBody] CreateVendorRatingRequest request, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var created = await _dispatch.RateAsync(scope, id, request, ct);
        return created == null
            ? NotFound(new { error = "Vendor not found" })
            : CreatedAtAction(nameof(Scorecard), new { id }, created);
    }

    /// <summary>
    /// Text the vendor a friendly request to send their W-9 for 1099 tax reporting. Requires a phone
    /// number on file (400 when absent). The SMS going out is the action — nothing is stored on the vendor.
    /// </summary>
    [HttpPost("{id:int}/request-w9")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestW9(
        int id,
        [FromBody] RequestVendorW9Request request,
        CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var result = await _service.RequestW9Async(
            scope, id, request.ClientOperationId, GetUserId(), ct);
        return result.Outcome switch
        {
            RequestW9Outcome.Queued => Ok(new { queued = true, sentTo = result.Phone }),
            RequestW9Outcome.VendorHasNoPhone => BadRequest(new
            {
                error = "This vendor has no phone number on file. Add a phone number, then request the W-9."
            }),
            _ => NotFound(new { error = "Vendor not found" }),
        };
    }

    /// <summary>Vendor performance scorecard: rating, jobs completed, and average DONE response time.</summary>
    [HttpGet("{id:int}/scorecard")]
    [ProducesResponseType(typeof(VendorScorecardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorScorecardResponse>> Scorecard(int id, CancellationToken ct)
    {
        if (!TryReadWorkspaceScope(out var scope)) return Forbid();
        var card = await _dispatch.GetScorecardAsync(scope, id, ct);
        return card == null ? NotFound(new { error = "Vendor not found" }) : Ok(card);
    }
}
