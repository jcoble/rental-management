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
public class LoanController : AuthenticatedPortfolioControllerBase
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
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LoanListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoanListResponse>> ListPage(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Loan not found" }) : Ok(item);
    }

    [HttpGet("{id:int}/payments")]
    [ProducesResponseType(typeof(IReadOnlyList<LoanPaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<LoanPaymentResponse>>> Payments(int id, CancellationToken ct)
    {
        var payments = await _service.GetPaymentsAsync(GetPortfolioId(), id, ct);
        return payments == null ? NotFound(new { error = "Loan not found" }) : Ok(payments);
    }

    [HttpPost]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Create([FromBody] CreateLoanRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(LoanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LoanResponse>> Update(int id, [FromBody] UpdateLoanRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Loan not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Loan not found" });
    }
}
