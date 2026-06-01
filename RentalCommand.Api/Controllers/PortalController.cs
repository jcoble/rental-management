using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Tenant-facing portal: read-only views of the signed-in tenant's own leases, balance, payments, work
/// orders, and messages. Scope is taken entirely from JWT claims (<c>portfolioId</c> + <c>tenantId</c>),
/// never from request parameters. A caller without a <c>tenantId</c> claim (e.g. staff/admin) gets 403 —
/// those users manage data through the staff-facing controllers instead.
/// </summary>
[ApiController]
[Route("api/v1/portal")]
[Produces("application/json")]
public class PortalController : AuthenticatedPortfolioControllerBase
{
    private readonly IPortalService _service;

    public PortalController(IPortalService service)
    {
        _service = service;
    }

    /// <summary>Tenant id from the <c>tenantId</c> JWT claim, or null when the caller is not a tenant.</summary>
    private int? GetTenantId()
    {
        var claim = User.FindFirst("tenantId");
        return claim != null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    [HttpGet("leases")]
    [ProducesResponseType(typeof(IReadOnlyList<LeaseResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<LeaseResponse>>> Leases(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetLeasesAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    [HttpGet("balance")]
    [ProducesResponseType(typeof(PortalBalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PortalBalanceResponse>> Balance(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var balance = await _service.GetBalanceAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(balance);
    }

    [HttpGet("payments")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalPaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PortalPaymentResponse>>> Payments(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetPaymentsAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    [HttpGet("work-orders")]
    [ProducesResponseType(typeof(IReadOnlyList<WorkOrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponse>>> WorkOrders(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetWorkOrdersAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    [HttpGet("messages")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalMessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PortalMessageResponse>>> Messages(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetMessagesAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    [HttpPost("messages")]
    [ProducesResponseType(typeof(PortalMessageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalMessageResponse>> CreateMessage(
        [FromBody] CreatePortalMessageRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            return BadRequest(new { error = "Subject cannot be empty" });
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return BadRequest(new { error = "Body cannot be empty" });
        }

        var created = await _service.CreateMessageAsync(GetPortfolioId(), tenantId.Value, request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property not found in this portfolio, or tenant has no portal account" })
            : CreatedAtAction(nameof(Messages), created);
    }

    [HttpPatch("messages/{id:int}")]
    [ProducesResponseType(typeof(PortalMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalMessageResponse>> UpdateMessageStatus(
        int id, [FromBody] UpdatePortalMessageStatusRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var updated = await _service.UpdateMessageStatusAsync(GetPortfolioId(), tenantId.Value, id, request.Status, ct);
        return updated == null ? NotFound(new { error = "Message not found" }) : Ok(updated);
    }
}
