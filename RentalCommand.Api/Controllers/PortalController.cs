using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;

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
    private readonly IStripePaymentService _stripe;

    public PortalController(
        IPortalService service,
        IConversationService conversations,
        IStripePaymentService stripe)
    {
        _service = service;
        _conversations = conversations;
        _stripe = stripe;
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

    [HttpGet("appointments")]
    [ProducesResponseType(typeof(IReadOnlyList<AppointmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AppointmentResponse>>> Appointments(CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetAppointmentsAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    /// <summary>
    /// Answers a tenant's plain-English question grounded in their OWN lease (rent, dates, deposit,
    /// late fee, notes). Scope is the tenant's lease only: an explicit <c>leaseId</c> that isn't theirs
    /// — or no lease at all — returns 404, never another tenant's lease. Falls back to a deterministic
    /// answer when no LLM key is configured.
    /// </summary>
    [HttpPost("lease/ask")]
    [ProducesResponseType(typeof(LeaseQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseQuestionResponse>> AskLease(
        [FromQuery] int? leaseId, [FromBody] LeaseQuestionRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var answer = await _service.AskLeaseAsync(GetPortfolioId(), tenantId.Value, leaseId, request.Question, ct);
        return answer == null
            ? NotFound(new { error = "No lease was found for this tenant, or the question was empty." })
            : Ok(answer);
    }

    // -----------------------------------------------------------------------------------------------
    // Online rent payment (optional convenience). Hosted Stripe Checkout — neither the web nor the
    // Flutter app needs a payment SDK; the tenant is redirected to {checkoutUrl}. Every path is
    // ownership-checked (a tenant pays only their own rent) and Stripe-gated (503 when not configured).
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a hosted Stripe Checkout session (card or ACH) for ONE of the signed-in tenant's own
    /// rent payments and returns <c>{ checkoutUrl }</c> to redirect to. A payment that isn't on this
    /// tenant's lease returns 404 (never reveals another tenant's payment). 503 when Stripe is off.
    /// </summary>
    [HttpPost("payments/{paymentId:int}/checkout")]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreatePaymentCheckout(
        int paymentId, [FromBody] PortalCheckoutRequest? request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _stripe.CreatePaymentCheckoutSessionAsync(
            GetPortfolioId(), tenantId.Value, paymentId, request?.SuccessUrl, request?.CancelUrl, ct);

        return result.Result switch
        {
            CheckoutResult.Outcome.NotEnabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." }),
            CheckoutResult.Outcome.NotFound =>
                NotFound(new { error = "Payment not found" }),
            _ => Ok(new CheckoutSessionResponse { CheckoutUrl = result.CheckoutUrl! }),
        };
    }

    /// <summary>
    /// Tenant's autopay enrollment status for a lease (defaults to their most relevant lease when
    /// <c>leaseId</c> is omitted). Returns Active=false when not enrolled.
    /// </summary>
    [HttpGet("autopay")]
    [ProducesResponseType(typeof(AutopayStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAutopay([FromQuery] int? leaseId, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var portfolioId = GetPortfolioId();
        var status = await _service.GetAutopayStatusAsync(portfolioId, tenantId.Value, leaseId, ct);
        if (status == null)
        {
            return NotFound(new { error = "Lease not found" });
        }

        status.OnlinePaymentsAvailable = await _stripe.IsOnlinePaymentsAvailableAsync(portfolioId, ct);
        return Ok(status);
    }

    /// <summary>
    /// Enrolls one of the tenant's own leases in autopay: starts a setup-mode Checkout session that
    /// saves a reusable payment method, and returns <c>{ checkoutUrl }</c>. The enrollment is only
    /// recorded once the setup session completes (webhook). 404 if the lease isn't the tenant's;
    /// 503 when Stripe is off.
    /// </summary>
    [HttpPost("autopay/enroll")]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnrollAutopay([FromBody] AutopayEnrollRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _stripe.CreateAutopaySetupSessionAsync(
            GetPortfolioId(), tenantId.Value, request.LeaseId, request.SuccessUrl, request.CancelUrl, ct);

        return result.Result switch
        {
            CheckoutResult.Outcome.NotEnabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." }),
            CheckoutResult.Outcome.NotFound =>
                NotFound(new { error = "Lease not found" }),
            _ => Ok(new CheckoutSessionResponse { CheckoutUrl = result.CheckoutUrl! }),
        };
    }

    /// <summary>
    /// Cancels autopay on one of the tenant's own leases (deactivates the enrollment so the Engine
    /// stops charging). 404 if the lease isn't the tenant's. Not Stripe-gated — purely local state.
    /// </summary>
    [HttpPost("autopay/cancel")]
    [ProducesResponseType(typeof(AutopayStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelAutopay([FromBody] AutopayCancelRequest request, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var portfolioId = GetPortfolioId();
        var status = await _service.CancelAutopayAsync(portfolioId, tenantId.Value, request.LeaseId, ct);
        if (status == null)
        {
            return NotFound(new { error = "Lease not found" });
        }

        status.OnlinePaymentsAvailable = await _stripe.IsOnlinePaymentsAvailableAsync(portfolioId, ct);
        return Ok(status);
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

    /// <summary>
    /// One of the signed-in tenant's OWN work orders with its live status timeline (Received →
    /// Assigned → In Progress → Done). Ownership is enforced server-side: a work order that isn't on
    /// this tenant's lease/unit returns 404, so a tenant can never read another tenant's work order.
    /// </summary>
    [HttpGet("work-orders/{id:int}")]
    [ProducesResponseType(typeof(WorkOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkOrderDetailResponse>> WorkOrder(int id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var item = await _service.GetWorkOrderDetailAsync(GetPortfolioId(), tenantId.Value, id, ct);
        return item == null ? NotFound(new { error = "Work order not found" }) : Ok(item);
    }

    [HttpPost("tenant/work-orders")]
    [ProducesResponseType(typeof(WorkOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WorkOrderResponse>> CreateTenantWorkOrder(
        [FromBody] CreateTenantWorkOrderRequest request,
        CancellationToken ct)
    {
        var tenantId = GetTenantId();
        if (tenantId == null)
        {
            return Forbid();
        }

        var created = await _service.CreateTenantWorkOrderAsync(GetPortfolioId(), tenantId.Value, request, ct);
        return created == null
            ? BadRequest(new { error = "No lease was found for this tenant." })
            : Created($"/api/v1/portal/work-orders/{created.Id}", created);
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
