using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only compatibility name for canonical TenantAccount receipt projections. New money is posted
/// only through <see cref="TenantAccountMoneyController"/> append commands; this surface deliberately
/// exposes no legacy Payment create/edit/status/delete action.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[Produces("application/json")]
public class PaymentController : ManagementControllerBase
{
    private readonly IPaymentService _service;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public PaymentController(IPaymentService service, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentReceiptResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PaymentReceiptResponse>>> List(
        [FromQuery] PaymentListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(PaymentListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentListResponse>> ListPage(
        [FromQuery] PaymentListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(PaymentReceiptResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentReceiptResponse>> Get(long id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Payment not found" }) : Ok(item);
    }

    // GET /api/v1/payments/{id}/scan[?thumb=true] — stream the original scanned check/document.
    [HttpGet("{id:int}/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetScan(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
        => ServeEntityScanAsync(_db, _files, "Payment", id, thumb, ct);

}
