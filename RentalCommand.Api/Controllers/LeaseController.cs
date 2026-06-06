using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for leases within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?tenantId&amp;propertyId&amp;skip&amp;take&amp;search&amp;sort</c>. Create validates the
/// referenced property, unit, and tenant are in the portfolio. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/leases")]
[Produces("application/json")]
public class LeaseController : AuthenticatedPortfolioControllerBase
{
    private readonly ILeaseService _service;
    private readonly ILeaseQaService _qa;
    private readonly ILeaseEsignService _esign;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public LeaseController(ILeaseService service, ILeaseQaService qa, ILeaseEsignService esign, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _qa = qa;
        _esign = esign;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LeaseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LeaseResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? tenantId, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), tenantId, propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(LeaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Lease not found" }) : Ok(item);
    }

    // GET /api/v1/leases/{id}/scan[?thumb=true] — stream the original scanned lease document.
    // Distinct from {id}/document (the generated/e-signed lease) and {id}/signed-document.
    [HttpGet("{id:int}/scan")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> GetScan(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
        => ServeEntityScanAsync(_db, _files, "Lease", id, thumb, ct);

    /// <summary>
    /// Tenant-facing ledger for a lease: every charge and payment, newest first, each with a
    /// plain-English explanation of what it is, plus a running balance. Kills "what is this charge?"
    /// disputes. Returns 404 when the lease is not in the caller's portfolio. When the caller is a
    /// tenant, they may only read their OWN lease's ledger (any other lease 404s) — this management
    /// endpoint is otherwise reachable by any authenticated portfolio user.
    /// </summary>
    [HttpGet("{id:int}/ledger")]
    [ProducesResponseType(typeof(LeaseLedgerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseLedgerResponse>> Ledger(int id, CancellationToken ct)
    {
        var ledger = await _service.GetLedgerAsync(GetPortfolioId(), id, GetTenantIdOrNull(), ct);
        return ledger == null ? NotFound(new { error = "Lease not found" }) : Ok(ledger);
    }

    [HttpPost]
    [ProducesResponseType(typeof(LeaseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseResponse>> Create([FromBody] CreateLeaseRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Referenced property, unit, or tenant not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(LeaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseResponse>> Update(int id, [FromBody] UpdateLeaseRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Lease not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Lease not found" });
    }

    [HttpPost("{id:int}/ask")]
    [ProducesResponseType(typeof(LeaseQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseQuestionResponse>> Ask(int id, [FromBody] LeaseQuestionRequest request, CancellationToken ct)
    {
        var answer = await _qa.AskAsync(GetPortfolioId(), id, request.Question, ct);
        return answer == null ? NotFound(new { error = "Lease not found or question is empty" }) : Ok(answer);
    }

    /// <summary>
    /// Generate a standard residential lease agreement PDF from the lease's captured terms (the
    /// "5-question generator"), store it as a document attached to the lease, and return its reference.
    /// </summary>
    [HttpPost("{id:int}/generate-document")]
    [ProducesResponseType(typeof(LeaseDocumentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseDocumentResponse>> GenerateDocument(int id, CancellationToken ct)
    {
        var doc = await _service.GenerateDocumentAsync(GetPortfolioId(), id, ct);
        return doc == null
            ? NotFound(new { error = "Lease not found" })
            : CreatedAtAction(nameof(Document), new { id }, doc);
    }

    /// <summary>Download the latest generated lease agreement PDF (404 until one has been generated).</summary>
    [HttpGet("{id:int}/document", Name = nameof(Document))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Document(int id, CancellationToken ct)
    {
        var file = await _service.GetDocumentAsync(GetPortfolioId(), id, ct);
        if (file == null)
        {
            return NotFound(new { error = "No generated lease agreement; generate it first." });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{file.Value.FileName}\"";
        return File(file.Value.Stream, file.Value.ContentType);
    }

    /// <summary>
    /// Send the lease's generated agreement out for electronic signature. Generates the agreement PDF first
    /// if none exists, defaults the signer to the lease's tenant, marks the lease
    /// <c>EsignStatus=Sent</c> / <c>LeaseStatus=PendingSignature</c>, and returns the signature snapshot.
    /// Returns 503 when the e-sign provider is not configured (gated) — the lease is left unchanged.
    /// </summary>
    [HttpPost("{id:int}/send-for-signature")]
    [ProducesResponseType(typeof(LeaseSignatureStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SendForSignature(int id, [FromBody] SendForSignatureRequest request, CancellationToken ct)
    {
        var result = await _esign.SendForSignatureAsync(
            GetPortfolioId(), id, request ?? new SendForSignatureRequest(),
            GetUserId(), HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        return result.Outcome switch
        {
            SendForSignatureOutcome.Sent => Ok(result.Status),
            SendForSignatureOutcome.NotFound => NotFound(new { error = result.Error }),
            SendForSignatureOutcome.MissingSigner => BadRequest(new { error = result.Error }),
            SendForSignatureOutcome.NotConfigured =>
                StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error }),
        };
    }

    /// <summary>
    /// Current signature status for the lease: <c>{ esignStatus, leaseStatus, envelopeId, hasSignedDocument }</c>.
    /// When the provider is configured and a request is in flight, the status is refreshed from the provider.
    /// </summary>
    [HttpGet("{id:int}/signature-status")]
    [ProducesResponseType(typeof(LeaseSignatureStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseSignatureStatusResponse>> SignatureStatus(int id, CancellationToken ct)
    {
        var status = await _esign.GetSignatureStatusAsync(GetPortfolioId(), id, ct);
        return status == null ? NotFound(new { error = "Lease not found" }) : Ok(status);
    }

    /// <summary>Download the stored fully-signed agreement PDF (404 until a signed document has been stored).</summary>
    [HttpGet("{id:int}/signed-document")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SignedDocument(int id, CancellationToken ct)
    {
        var file = await _esign.GetSignedDocumentAsync(GetPortfolioId(), id, ct);
        if (file == null)
        {
            return NotFound(new { error = "No signed agreement on file for this lease." });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{file.Value.FileName}\"";
        return File(file.Value.Stream, file.Value.ContentType);
    }
}
