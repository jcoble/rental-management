using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for payments within the caller's portfolio plus the mark-paid action. Scope comes from the JWT
/// <c>portfolioId</c> claim; list supports <c>?leaseId&amp;applicationId&amp;skip&amp;take&amp;search&amp;sort&amp;dueFrom&amp;dueTo&amp;paidFrom&amp;paidTo</c>. Create
/// validates the referenced lease is in the portfolio. Payment has no soft-delete, so removal is a hard delete.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[Produces("application/json")]
public class PaymentController : ManagementControllerBase
{
    private readonly IPaymentService _service;
    private readonly IStripePaymentService _stripeService;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public PaymentController(IPaymentService service, IStripePaymentService stripeService, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _stripeService = stripeService;
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

    [HttpPost]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentResponse>> Create([FromBody] CreatePaymentRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced lease not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentResponse>> Update(int id, [FromBody] UpdatePaymentRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Payment not found" }) : Ok(updated);
    }

    [HttpPost("{id:int}/mark-paid")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PaymentResponse>> MarkPaid(int id, [FromBody] MarkPaidRequest? request, CancellationToken ct)
    {
        var updated = await _service.MarkPaidAsync(GetPortfolioId(), id, request ?? new MarkPaidRequest(), ct);
        return updated == null ? NotFound(new { error = "Payment not found" }) : Ok(updated);
    }

    [HttpPost("leases/{leaseId:int}/past-due/mark-paid")]
    [ProducesResponseType(typeof(MarkLeasePastDuePaidResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarkLeasePastDuePaidResponse>> MarkLeasePastDuePaid(
        int leaseId,
        [FromBody] MarkPaidRequest? request,
        CancellationToken ct)
    {
        var result = await _service.MarkLeasePastDuePaidAsync(GetPortfolioId(), leaseId, request ?? new MarkPaidRequest(), ct);
        return result == null ? NotFound(new { error = "Lease not found" }) : Ok(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Payment not found" });
    }

    /// <summary>
    /// Creates a Stripe PaymentIntent for the given payment and returns the client secret needed
    /// by the front end to complete the card collection step. Returns 503 when Stripe is not configured.
    /// </summary>
    [HttpPost("{id:int}/create-intent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreatePaymentIntent(int id, CancellationToken ct)
    {
        // TODO: also verify the payment's lease belongs to the calling tenant
        var result = await _stripeService.CreatePaymentIntentAsync(GetPortfolioId(), id, ct);

        return result.Result switch
        {
            CreateIntentResult.Outcome.NotEnabled =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." }),
            CreateIntentResult.Outcome.NotFound =>
                NotFound(new { error = "Payment not found" }),
            _ =>
                Ok(new
                {
                    clientSecret = result.ClientSecret,
                    publishableKey = result.PublishableKey,
                    transactionId = result.TransactionId
                })
        };
    }
}
