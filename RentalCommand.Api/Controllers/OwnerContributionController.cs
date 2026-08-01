using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>CRUD for owner contributions: owner cash funding that increases equity.</summary>
[ApiController]
[Route("api/v1/owner-contributions")]
[Produces("application/json")]
public sealed class OwnerContributionController : ManagementControllerBase
{
    private readonly IOwnerContributionService _service;

    public OwnerContributionController(IOwnerContributionService service) => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerContributionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OwnerContributionResponse>>> List(
        [FromQuery] OwnerContributionListQuery query,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        return Ok(await _service.ListAsync(scope, query, ct));
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(AccountingPage<OwnerContributionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountingPage<OwnerContributionResponse>>> ListPage(
        [FromQuery] OwnerContributionListQuery query,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        return Ok(await _service.ListPageAsync(scope, query, ct));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(OwnerContributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContributionResponse>> Get(int id, CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        var contribution = await _service.GetAsync(scope, id, ct);
        return contribution is null
            ? NotFound(new { error = "Owner contribution not found" })
            : Ok(contribution);
    }

    [HttpPost]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerContributionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContributionResponse>> Create(
        [FromBody] CreateOwnerContributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var contribution = await _service.CreateAsync(scope, request, operationKey, ct);
            return contribution is null
                ? NotFound(new { error = "Referenced owner or property not found in this portfolio" })
                : CreatedAtAction(nameof(Get), new { id = contribution.Id }, contribution);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (DomainValidationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPatch("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerContributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContributionResponse>> Update(
        int id,
        [FromBody] UpdateOwnerContributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var contribution = await _service.UpdateAsync(scope, id, request, operationKey, ct);
            return contribution is null
                ? NotFound(new { error = "Owner contribution not found" })
                : Ok(contribution);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (DomainValidationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerContributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContributionResponse>> Approve(
        int id,
        [FromBody] ApproveOwnerContributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var contribution = await _service.ApproveAsync(scope, id, request, operationKey, ct);
            return contribution is null
                ? NotFound(new { error = "Owner contribution not found" })
                : Ok(contribution);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (DomainValidationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/reject")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [ProducesResponseType(typeof(OwnerContributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerContributionResponse>> Reject(
        int id,
        [FromBody] RejectOwnerContributionRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var contribution = await _service.RejectAsync(scope, id, request, operationKey, ct);
            return contribution is null
                ? NotFound(new { error = "Owner contribution not found" })
                : Ok(contribution);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (DomainValidationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage)]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryReadManagementScope(out var scope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            return await _service.DeleteAsync(scope, id, operationKey, ct)
                ? NoContent()
                : NotFound(new { error = "Owner contribution not found" });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (DomainValidationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
