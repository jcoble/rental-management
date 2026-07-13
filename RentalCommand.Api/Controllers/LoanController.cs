using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for per-property loans (mortgages) within the caller's portfolio, plus read access to a
/// loan's amortization schedule. Scope comes from the server-validated workspace context; list supports
/// <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort&amp;from&amp;to</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/loans")]
[Produces("application/json")]
public class LoanController : ManagementControllerBase
{
    private readonly ILoanService _service;

    public LoanController(ILoanService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LoanResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LoanResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetWorkspaceReadScope(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LoanListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoanListResponse>> ListPage(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetWorkspaceReadScope(), propertyId, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Loan not found" }) : Ok(item);
    }

    [HttpGet("{id:int}/payments")]
    [ProducesResponseType(typeof(IReadOnlyList<LoanPaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LoanPaymentResponse>>> Payments(int id, CancellationToken ct)
    {
        var payments = await _service.GetPaymentsAsync(GetWorkspaceReadScope(), id, ct);
        return payments == null ? NotFound(new { error = "Loan not found" }) : Ok(payments);
    }

    [HttpPost]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Create(
        [FromBody] CreateLoanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var created = await _service.CreateAsync(GetWorkspaceReadScope(), request, operationKey, ct);
            return created == null
                ? NotFound(new { error = "Referenced property not found in this portfolio" })
                : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Update(
        int id, [FromBody] UpdateLoanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var updated = await _service.UpdateAsync(GetWorkspaceReadScope(), id, request, operationKey, ct);
            return updated == null ? NotFound(new { error = "Loan not found" }) : Ok(updated);
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
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var deleted = await _service.DeleteAsync(GetWorkspaceReadScope(), id, operationKey, ct);
            return deleted ? NoContent() : NotFound(new { error = "Loan not found" });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }
}
