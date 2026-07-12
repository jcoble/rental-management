using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only canonical TenantAccount receipt projections. New money is posted only through
/// <see cref="TenantAccountMoneyController"/> append commands; this surface deliberately exposes no
/// mutable Payment create/edit/status/delete action.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[Produces("application/json")]
public class PaymentController : AuthenticatedPortfolioControllerBase
{
    private readonly IPaymentReceiptQueryService _service;

    public PaymentController(IPaymentReceiptQueryService service) => _service = service;

    [HttpGet("account-options")]
    [ProducesResponseType(typeof(TenantAccountOptionListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantAccountOptionListResponse>> ListAccountOptions(
        [FromQuery] TenantAccountOptionQuery query, CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        return Ok(await _service.ListAccountOptionsAsync(access, query, ct));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PaymentReceiptResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PaymentReceiptResponse>>> List(
        [FromQuery] PaymentListQuery query, CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var items = await _service.ListAsync(access, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(PaymentListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaymentListResponse>> ListPage(
        [FromQuery] PaymentListQuery query, CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var page = await _service.ListPageAsync(access, query, ct);
        return Ok(page);
    }

    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(PaymentReceiptResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentReceiptResponse>> Get(long id, CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var item = await _service.GetAsync(access, id, ct);
        return item == null ? NotFound(new { error = "Payment not found" }) : Ok(item);
    }

    private bool TryReadAccessContext(out PaymentReceiptReadContext access)
    {
        access = default;
        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)
            || !int.TryParse(User.FindFirstValue("ctx"), out var accessContextId)
            || !long.TryParse(User.FindFirstValue("ar"), out var accessRevision))
        {
            return false;
        }

        access = new PaymentReceiptReadContext(
            GetPortfolioId(), GetUserId(), sessionId, accessContextId, accessRevision);
        return true;
    }

}
