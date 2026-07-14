using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Tenant-facing portal: read-only views of the signed-in tenant's relationships, agreements, tenant
/// accounts, work orders, and messages. Scope comes from the validated context and its effective tenant relationship,
/// never from request parameters or a tenant-id token claim. A context without that relationship gets 403 —
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

    private Task<int?> GetTenantIdAsync(CancellationToken ct) =>
        _service.ResolveTenantIdAsync(GetPortfolioId(), GetAccessContextId(), ct);

    private PortalTenantReadScope GetTenantReadScope()
    {
        var active = GetActiveAccessContext();
        return new PortalTenantReadScope(
            active.PortfolioId,
            active.UserId,
            active.AccessContextId,
            active.AccessRevision);
    }

    [HttpGet("leases")]
    [ProducesResponseType(typeof(IReadOnlyList<PortalLeaseRelationshipResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<PortalLeaseRelationshipResponse>>> Leases(CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetLeasesAsync(
            GetPortfolioId(), GetAccessContextId(), tenantId.Value, ct);
        return Ok(items);
    }

    [HttpGet("tenant-accounts/page")]
    [ProducesResponseType(typeof(PortalTenantAccountPageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PortalTenantAccountPageResponse>> TenantAccountsPage(
        [FromQuery] PortalTenantAccountListQuery query,
        CancellationToken ct) =>
        Ok(await _service.ListTenantAccountsPageAsync(GetTenantReadScope(), query, ct));

    [HttpGet("tenant-accounts/{id:int}")]
    [ProducesResponseType(typeof(PortalTenantAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalTenantAccountResponse>> TenantAccount(
        int id,
        CancellationToken ct)
    {
        var account = await _service.GetTenantAccountAsync(GetTenantReadScope(), id, ct);
        return account is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(account);
    }

    [HttpGet("tenant-accounts/{id:int}/entries/page")]
    [ProducesResponseType(typeof(PortalTenantLedgerEntryPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalTenantLedgerEntryPageResponse>> TenantAccountEntriesPage(
        int id,
        [FromQuery] PortalTenantLedgerEntryListQuery query,
        CancellationToken ct)
    {
        var page = await _service.ListTenantAccountEntriesPageAsync(
            GetTenantReadScope(), id, query, ct);
        return page is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(page);
    }

    [HttpGet("tenant-accounts/{id:int}/charges/page")]
    [ProducesResponseType(typeof(PortalTenantChargePageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalTenantChargePageResponse>> TenantAccountChargesPage(
        int id,
        [FromQuery] PortalTenantChargeListQuery query,
        CancellationToken ct)
    {
        var page = await _service.ListTenantAccountChargesPageAsync(
            GetTenantReadScope(), id, query, ct);
        return page is null
            ? NotFound(new { error = "Tenant account not found" })
            : Ok(page);
    }

    [HttpGet("tenant-accounts/{id:int}/deposit")]
    [ProducesResponseType(typeof(PortalTenantAccountDepositResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalTenantAccountDepositResponse>> TenantAccountDeposit(
        int id,
        CancellationToken ct)
    {
        var deposit = await _service.GetTenantAccountDepositAsync(GetTenantReadScope(), id, ct);
        return deposit is null
            ? NotFound(new { error = "Tenant account deposit not found" })
            : Ok(deposit);
    }

    [HttpGet("appointments")]
    [ProducesResponseType(typeof(IReadOnlyList<AppointmentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AppointmentResponse>>> Appointments(CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _service.GetAppointmentsAsync(
            GetPortfolioId(), GetAccessContextId(), tenantId.Value, ct);
        return Ok(items);
    }

    /// <summary>
    /// Answers a tenant's plain-English question from the governing executed agreement on one of
    /// their effective rental relationships. The optional selector is a LeaseManagement id.
    /// </summary>
    [HttpPost("lease/ask")]
    [ProducesResponseType(typeof(LeaseQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseQuestionResponse>> AskLease(
        [FromQuery] int? leaseManagementId,
        [FromBody] LeaseQuestionRequest request,
        CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var answer = await _service.AskLeaseAsync(
            GetPortfolioId(),
            GetAccessContextId(),
            tenantId.Value,
            leaseManagementId,
            request.Question,
            ct);
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
    /// Starts hosted Stripe Checkout for one open charge on the signed-in tenant's canonical account.
    /// A charge outside that account relationship returns 404. 503 when Stripe is off.
    /// </summary>
    [HttpPost("tenant-accounts/{tenantAccountId:int}/charges/{chargeLedgerEntryId:long}/checkout")]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreatePaymentCheckout(
        int tenantAccountId, long chargeLedgerEntryId,
        [FromBody] PortalCheckoutRequest? request, CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _stripe.CreatePaymentCheckoutSessionAsync(
            GetPortfolioId(), tenantId.Value, tenantAccountId, chargeLedgerEntryId, GetUserId(),
            request?.SuccessUrl, request?.CancelUrl, ct);

        return result.Result switch
        {
            CheckoutResult.Outcome.NotEnabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." }),
            CheckoutResult.Outcome.NotFound =>
                NotFound(new { error = "Tenant account charge not found" }),
            _ => Ok(new CheckoutSessionResponse { CheckoutUrl = result.CheckoutUrl! }),
        };
    }

    /// <summary>
    /// Tenant's autopay enrollment status for one canonical tenant account.
    /// </summary>
    [HttpGet("tenant-accounts/{tenantAccountId:int}/autopay")]
    [ProducesResponseType(typeof(AutopayStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAutopay(int tenantAccountId, CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var portfolioId = GetPortfolioId();
        var status = await _service.GetAutopayStatusAsync(portfolioId, tenantId.Value, tenantAccountId, ct);
        if (status == null)
        {
            return NotFound(new { error = "Tenant account not found" });
        }

        status.OnlinePaymentsAvailable = await _stripe.IsOnlinePaymentsAvailableAsync(portfolioId, ct);
        return Ok(status);
    }

    /// <summary>
    /// Enrolls one of the tenant's own canonical accounts in autopay: starts setup Checkout that
    /// saves a reusable payment method, and returns <c>{ checkoutUrl }</c>. The enrollment is only
    /// recorded once the setup session completes (webhook). 404 if the account isn't the tenant's;
    /// 503 when Stripe is off.
    /// </summary>
    [HttpPost("tenant-accounts/{tenantAccountId:int}/autopay/enroll")]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> EnrollAutopay(
        int tenantAccountId, [FromBody] AutopayEnrollRequest request, CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _stripe.CreateAutopaySetupSessionAsync(
            GetPortfolioId(), tenantId.Value, tenantAccountId, GetUserId(), request.OperationKey,
            request.SuccessUrl, request.CancelUrl, ct);

        return result.Result switch
        {
            CheckoutResult.Outcome.NotEnabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." }),
            CheckoutResult.Outcome.NotFound =>
                NotFound(new { error = "Tenant account not found" }),
            _ => Ok(new CheckoutSessionResponse { CheckoutUrl = result.CheckoutUrl! }),
        };
    }

    /// <summary>
    /// Cancels autopay on one of the tenant's own canonical accounts.
    /// </summary>
    [HttpPost("tenant-accounts/{tenantAccountId:int}/autopay/cancel")]
    [ProducesResponseType(typeof(AutopayStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelAutopay(
        int tenantAccountId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var operationKey = idempotencyKey?.Trim() ?? string.Empty;
        if (operationKey.Length is <= 0 or > 128)
        {
            return BadRequest(new
            {
                error = "Idempotency-Key is required and must be at most 128 characters.",
            });
        }
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var active = GetActiveAccessContext();
        var portfolioId = active.PortfolioId;
        var status = await _service.CancelAutopayAsync(
            active, tenantId.Value, tenantAccountId, operationKey, ct);
        if (status == null)
        {
            return NotFound(new { error = "Tenant account not found" });
        }

        status.OnlinePaymentsAvailable = await _stripe.IsOnlinePaymentsAvailableAsync(portfolioId, ct);
        return Ok(status);
    }

    [HttpGet("work-orders")]
    [ProducesResponseType(typeof(PortalTenantWorkOrderPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PortalTenantWorkOrderPageResponse>> WorkOrders(
        [FromQuery] PortalTenantWorkOrderListQuery query,
        CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        return Ok(await _service.ListWorkOrdersPageAsync(
            GetTenantReadScope(), tenantId.Value, query, ct));
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
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var item = await _service.GetWorkOrderDetailAsync(
            GetTenantReadScope(), tenantId.Value, id, ct);
        return item == null ? NotFound(new { error = "Work order not found" }) : Ok(item);
    }

    [HttpPost("tenant/work-orders")]
    [ProducesResponseType(typeof(WorkOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<WorkOrderResponse>> CreateTenantWorkOrder(
        [FromBody] CreateTenantWorkOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var operationKey = idempotencyKey?.Trim() ?? string.Empty;
        if (operationKey.Length is <= 0 or > 128)
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        var created = await _service.CreateTenantWorkOrderAsync(
            GetActiveAccessContext(), request, operationKey, ct);
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
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var items = await _conversations.ListForTenantAsync(GetPortfolioId(), tenantId.Value, ct);
        return Ok(items);
    }

    /// <summary>
    /// Searchable, filterable tenant inbox page. The tenant boundary, filters, sort, count, and
    /// requested window are all applied by the database query.
    /// </summary>
    [HttpGet("conversations/page")]
    [ProducesResponseType(typeof(ConversationListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ConversationListResponse>> ConversationPage(
        [FromQuery] ConversationListQuery query,
        CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var page = await _conversations.ListPageForTenantAsync(
            GetPortfolioId(), tenantId.Value, query, ct);
        return Ok(page);
    }

    /// <summary>Fetch one of the tenant's conversations with its full history.</summary>
    [HttpGet("conversations/{id:int}")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> Conversation(int id, CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var item = await _conversations.GetForTenantAsync(GetPortfolioId(), tenantId.Value, id, ct);
        return item == null ? NotFound(new { error = "Conversation not found" }) : Ok(item);
    }

    [HttpPost("conversations/{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkConversationRead(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null) return Forbid();
        var operationKey = idempotencyKey?.Trim() ?? string.Empty;
        if (operationKey.Length is <= 0 or > 128)
            return BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
        var active = GetActiveAccessContext();
        var scope = new WorkspaceReadScope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        var found = await _conversations.MarkReadForTenantAsync(
            scope, tenantId.Value, id, operationKey, ct);
        return found ? NoContent() : NotFound(new { error = "Conversation not found" });
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
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var created = await _conversations.TenantStartAsync(
            GetPortfolioId(), tenantId.Value, request.Subject, request.Body, request.OperationKey, ct);

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
        var tenantId = await GetTenantIdAsync(ct);
        if (tenantId == null)
        {
            return Forbid();
        }

        var result = await _conversations.TenantPostAsync(
            GetPortfolioId(), tenantId.Value, id, request.Body, request.OperationKey, ct);
        return result == null ? NotFound(new { error = "Conversation not found" }) : Ok(result);
    }
}
