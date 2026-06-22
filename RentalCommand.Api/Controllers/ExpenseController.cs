using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for expenses within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?propertyId&amp;unitId&amp;workOrderId&amp;skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/expenses")]
[Produces("application/json")]
public class ExpenseController : ManagementControllerBase
{
    private readonly IExpenseService _service;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public ExpenseController(IExpenseService service, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ExpenseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ExpenseResponse>>> List(
        [FromQuery] ListQuery query,
        [FromQuery] int? propertyId,
        [FromQuery] int? unitId,
        [FromQuery] int? workOrderId,
        CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, unitId, workOrderId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(ExpenseListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ExpenseListResponse>> ListPage(
        [FromQuery] ListQuery query,
        [FromQuery] int? propertyId,
        [FromQuery] int? unitId,
        [FromQuery] int? workOrderId,
        [FromQuery] bool workOrderLinkedOnly = false,
        CancellationToken ct = default)
    {
        var page = await _service.ListPageAsync(
            GetPortfolioId(), propertyId, unitId, workOrderId, workOrderLinkedOnly, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Expense not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Create([FromBody] CreateExpenseRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, vendor, or work order not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Update(int id, [FromBody] UpdateExpenseRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Expense not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Expense not found" });
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/expenses/{id}/receipt[?thumb=true] — stream the original scanned receipt.
    // Delegates to the shared base-controller helper (also used by payments/leases/work-orders).
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/receipt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetReceipt(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
        => ServeEntityScanAsync(_db, _files, "Expense", id, thumb, ct);
}
