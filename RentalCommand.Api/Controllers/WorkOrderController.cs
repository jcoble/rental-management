using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for maintenance work orders within the caller's canonical workspace and property scope.
/// The list supports <c>?propertyId&amp;vendorId&amp;skip&amp;take&amp;search&amp;sort</c>
/// plus requested/scheduled/completed date windows.
/// Create validates the referenced property/unit/tenant/lease relationship/vendor are in the portfolio. Work orders
/// are soft-deleted so their maintenance history remains available for audit and reporting.
/// </summary>
[ApiController]
[Route("api/v1/work-orders")]
[Produces("application/json")]
public class WorkOrderController : ManagementControllerBase
{
    private readonly IWorkOrderService _service;
    private readonly IVendorDispatchService _dispatch;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public WorkOrderController(IWorkOrderService service, IVendorDispatchService dispatch, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _dispatch = dispatch;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WorkOrderResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponse>>> List(
        [FromQuery] WorkOrderListQuery query, [FromQuery] int? propertyId, [FromQuery] int? unitId, [FromQuery] int? vendorId, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(
            GetWorkspaceReadScope(), propertyId, unitId, vendorId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(WorkOrderListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkOrderListResponse>> ListPage(
        [FromQuery] WorkOrderListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(WorkOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderDetailResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Work order not found" }) : Ok(item);
    }

    // GET /api/v1/work-orders/{id}/scan[?thumb=true] — stream the original scanned estimate/document.
    [HttpGet("{id:int}/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScan(
        int id,
        [FromQuery] bool thumb = false,
        CancellationToken ct = default)
    {
        var portfolioId = GetPortfolioId();
        if (!await HasAnyCapabilityAsync(
                [CapabilityKeys.WorkRead, CapabilityKeys.AssignedWorkRead],
                new WorkOrderCapabilityAuthorizationTarget(portfolioId, id),
                ct))
        {
            return NotFound(new { error = "Work order not found" });
        }

        return await ServeEntityScanAsync(_db, _files, "WorkOrder", id, thumb, ct);
    }

    [HttpPost]
    [ProducesResponseType(typeof(WorkOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderResponse>> Create(
        [FromBody] CreateWorkOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        if (!IsScheduleWindowValid(request.ScheduledFor, request.ScheduledWindowEnd))
        {
            return BadRequest(new { error = "The arrival window end must be after its start." });
        }

        var created = await _service.CreateAuthorizedAsync(
            GetWorkspaceReadScope(), request, operationKey, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, tenant, lease relationship, or vendor not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(WorkOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderResponse>> Update(
        int id, [FromBody] UpdateWorkOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        if (!IsScheduleWindowValid(request.ScheduledFor, request.ScheduledWindowEnd))
        {
            return BadRequest(new { error = "The arrival window end must be after its start." });
        }

        var updated = await _service.UpdateAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Work order not found" }) : Ok(updated);
    }

    [HttpPost("{id:int}/comments")]
    [ProducesResponseType(typeof(WorkOrderMutationReceipt), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderMutationReceipt>> Comment(
        int id,
        [FromBody] WorkOrderCommentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        var updated = await _service.CommentAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, operationKey, ct);
        return updated == null ? NotFound(new { error = "Work order not found" }) : Ok(updated);
    }

    /// <summary>
    /// Validates the scheduled arrival window: when both ends are supplied on the request,
    /// <paramref name="windowEnd"/> must be strictly after <paramref name="start"/>. A window-end with
    /// no start (or vice versa) is left to the partial-update semantics and not rejected here. Compared
    /// as instants, so the landlord's offset is honored.
    /// </summary>
    private static bool IsScheduleWindowValid(DateTimeOffset? start, DateTimeOffset? windowEnd)
        => !(start.HasValue && windowEnd.HasValue) || windowEnd.Value > start.Value;

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        var deleted = await _service.DeleteAuthorizedAsync(GetWorkspaceReadScope(), id, operationKey, ct);
        return deleted ? NoContent() : NotFound(new { error = "Work order not found" });
    }

    /// <summary>
    /// Assign the work order to a vendor and text them the job ("reply DONE when finished"). Creates an
    /// open dispatch and enqueues the SMS. 404 when the work order or vendor is out of scope; 400 when
    /// the vendor has no phone number on file.
    /// </summary>
    [HttpPost("{id:int}/dispatch")]
    [ProducesResponseType(typeof(VendorDispatchResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorDispatchResponse>> Dispatch(int id, [FromBody] DispatchWorkOrderRequest request, CancellationToken ct)
    {
        var result = await _dispatch.DispatchAuthorizedAsync(
            GetWorkspaceReadScope(), id, request, GetUserId(), ct);
        return result.Outcome switch
        {
            DispatchOutcome.Dispatched => CreatedAtAction(nameof(Get), new { id }, result.Dispatch),
            DispatchOutcome.VendorHasNoPhone => BadRequest(new { error = "Vendor has no phone number on file; add one before dispatching." }),
            DispatchOutcome.AlreadyDispatched => BadRequest(new { error = "This work order is already dispatched to this vendor; wait for their DONE reply or reassign it." }),
            _ => NotFound(new { error = "Work order or vendor not found in this portfolio" }),
        };
    }

    /// <summary>
    /// Cancel one still-open vendor dispatch without deleting the historical dispatch row.
    /// </summary>
    [HttpPost("{id:int}/dispatches/{dispatchId:int}/cancel")]
    [ProducesResponseType(typeof(CancelVendorDispatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CancelVendorDispatchResponse>> CancelDispatch(
        int id,
        int dispatchId,
        [FromBody] CancelVendorDispatchRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
        {
            return BadRequest(new { error = "IdempotencyKey is required and must be at most 100 characters." });
        }

        var result = await _dispatch.CancelAuthorizedAsync(
            GetWorkspaceReadScope(), id, dispatchId, request, GetUserId(), ct);
        return result.Outcome switch
        {
            CancelDispatchOutcome.Cancelled => Ok(result.Dispatch),
            CancelDispatchOutcome.AlreadyClosed => BadRequest(new { error = "This vendor dispatch is already closed." }),
            _ => NotFound(new { error = "Open vendor dispatch not found in this portfolio" }),
        };
    }

    /// <summary>
    /// Repairs an exact, still-pending vendor dispatch whose committed business chronology was
    /// contaminated by runtime wall-clock time. No replacement SMS is created.
    /// </summary>
    [HttpPost("{workOrderId:int}/dispatches/{dispatchId:int}/recover-chronology")]
    [ProducesResponseType(typeof(RecoverVendorDispatchChronologyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecoverVendorDispatchChronologyResponse>> RecoverDispatchChronology(
        int workOrderId,
        int dispatchId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecoverVendorDispatchChronologyRequest request,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey))
            return BadRequest(new
            {
                error = "Idempotency-Key is required and must be at most 128 characters.",
            });

        try
        {
            return Ok(await _dispatch.RecoverChronologyAuthorizedAsync(
                GetWorkspaceReadScope(),
                workOrderId,
                dispatchId,
                request,
                GetUserId(),
                operationKey,
                ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
