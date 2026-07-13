using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for expenses within the caller's portfolio. Scope comes from the server-validated workspace context;
/// list supports <c>?propertyId&amp;unitId&amp;workOrderId&amp;skip&amp;take&amp;search&amp;sort&amp;incurredFrom&amp;incurredTo&amp;dueFrom&amp;dueTo&amp;paidFrom&amp;paidTo</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/expenses")]
[Produces("application/json")]
public class ExpenseController : ManagementControllerBase
{
    private readonly IExpenseService _service;
    private readonly ICapitalAssetService _capitalAssets;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public ExpenseController(
        IExpenseService service,
        ICapitalAssetService capitalAssets,
        RentalCommandDbContext db,
        IFileStorage files)
    {
        _service = service;
        _capitalAssets = capitalAssets;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ExpenseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ExpenseResponse>>> List(
        [FromQuery] ExpenseListQuery query,
        [FromQuery] int? propertyId,
        [FromQuery] int? unitId,
        [FromQuery] int? workOrderId,
        CancellationToken ct)
    {
        var items = await _service.ListAsync(GetWorkspaceReadScope(), propertyId, unitId, workOrderId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(ExpenseListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ExpenseListResponse>> ListPage(
        [FromQuery] ExpenseListQuery query,
        [FromQuery] int? propertyId,
        [FromQuery] int? unitId,
        [FromQuery] int? workOrderId,
        [FromQuery] bool workOrderLinkedOnly = false,
        CancellationToken ct = default)
    {
        var page = await _service.ListPageAsync(
            GetWorkspaceReadScope(), propertyId, unitId, workOrderId, workOrderLinkedOnly, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Expense not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Create(
        [FromBody] CreateExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var created = await _service.CreateAsync(GetWorkspaceReadScope(), request, operationKey, ct);
            return created == null
                ? NotFound(new { error = "Referenced property, unit, vendor, or work order not found in this portfolio" })
                : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(ExpenseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExpenseResponse>> Update(
        int id, [FromBody] UpdateExpenseRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        try
        {
            var updated = await _service.UpdateAsync(GetWorkspaceReadScope(), id, request, operationKey, ct);
            return updated == null ? NotFound(new { error = "Expense not found" }) : Ok(updated);
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
            return deleted ? NoContent() : NotFound(new { error = "Expense not found" });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/capitalize")]
    [ProducesResponseType(typeof(CapitalAssetResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CapitalAssetResponse>> Capitalize(
        int id, [FromBody] CapitalizeExpenseRequest request, CancellationToken ct)
    {
        var scope = GetWorkspaceReadScope();
        var visible = await _service.GetAsync(scope, id, ct);
        if (visible?.PropertyId is not int propertyId)
            return NotFound(new { error = "Expense not found, already capitalized, or not linked to a property" });
        if (!await HasCapabilityAsync(
                RentalCommand.Core.Authorization.CapabilityKeys.MoneyExpensesManage,
                new RentalCommand.Core.Authorization.PropertyCapabilityAuthorizationTarget(
                    scope.PortfolioId, propertyId), ct))
            return StatusCode(403, new { error = "You can view this expense but cannot capitalize it." });
        var created = await _capitalAssets.CapitalizeExpenseAsync(GetPortfolioId(), id, request, ct);
        return created is null
            ? NotFound(new { error = "Expense not found, already capitalized, or not linked to a property" })
            : CreatedAtAction("Get", "CapitalAssets", new { id = created.Id }, created);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/expenses/{id}/receipt[?thumb=true] — stream the original scanned receipt.
    // Delegates to the shared base-controller helper (also used by payments/leases/work-orders).
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/receipt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReceipt(
        int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
    {
        if (await _service.GetAsync(GetWorkspaceReadScope(), id, ct) is null)
            return NotFound(new { error = "Document not found" });
        return await ServeEntityScanAsync(_db, _files, "Expense", id, thumb, ct);
    }
}
