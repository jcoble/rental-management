using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for leases within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?tenantId&amp;propertyId&amp;skip&amp;take&amp;search&amp;sort</c> plus
/// start/end/active-period date windows. Create validates the
/// referenced property, unit, and tenant are in the portfolio. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/leases")]
[Produces("application/json")]
public class LeaseController : ManagementControllerBase
{
    private readonly ILeaseService _service;
    private readonly ILeaseQaService _qa;
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;

    public LeaseController(ILeaseService service, ILeaseQaService qa, RentalCommandDbContext db, IFileStorage files)
    {
        _service = service;
        _qa = qa;
        _db = db;
        _files = files;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LeaseResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LeaseResponse>>> List(
        [FromQuery] LeaseListQuery query, [FromQuery] int? tenantId, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), tenantId, propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LeaseListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LeaseListResponse>> ListPage([FromQuery] LeaseListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
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

    [HttpPost("{id:int}/ask")]
    [ProducesResponseType(typeof(LeaseQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseQuestionResponse>> Ask(int id, [FromBody] LeaseQuestionRequest request, CancellationToken ct)
    {
        var answer = await _qa.AskAsync(GetPortfolioId(), id, request.Question, ct);
        return answer == null ? NotFound(new { error = "Lease not found or question is empty" }) : Ok(answer);
    }

}
