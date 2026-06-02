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
    private readonly IConversationService _conversations;

    public PortalController(IPortalService service, IConversationService conversations)
    {
        _service = service;
        _conversations = conversations;
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

    // -----------------------------------------------------------------------------------------------
    // Threaded conversations (tenant side). A tenant has multiple topic threads; each is scoped to the
    // signed-in tenant via the tenantId claim. Tenant messages are in-app only (no channel selection).
    // -----------------------------------------------------------------------------------------------

    /// <summary>List the signed-in tenant's conversations, most-recently-active first.</summary>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ConversationSummary>>> Conversations(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _conversations.ListForTenantAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    /// <summary>Fetch one of the tenant's conversations with its full history; resets the tenant's unread count.</summary>
    [HttpGet("conversations/{id:int}")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> Conversation(int id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var item = await _conversations.GetForTenantAsync(GetPortfolioId(), tenantId.Value, id, ct);
        return item == null ? NotFound(new { error = "Conversation not found" }) : Ok(item);
    }

    /// <summary>Tenant opens a new topic thread with the landlord.</summary>
    [HttpPost("conversations")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> StartConversation(
        [FromBody] TenantStartConversationRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var created = await _conversations.TenantStartAsync(
            GetPortfolioId(), tenantId.Value, request.Subject, request.Body, ct);

        return created == null
            ? NotFound(new { error = "Tenant not found" })
            : CreatedAtAction(nameof(Conversation), new { id = created.Id }, created);
    }

    /// <summary>Tenant appends a reply to one of their own conversations (in-app only).</summary>
    [HttpPost("conversations/{id:int}/messages")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> PostConversationMessage(
        int id, [FromBody] TenantPostMessageRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _conversations.TenantPostAsync(GetPortfolioId(), tenantId.Value, id, request.Body, ct);
        return result == null ? NotFound(new { error = "Conversation not found" }) : Ok(result);
    }
}
