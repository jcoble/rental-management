using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for vendors within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/vendors")]
[Produces("application/json")]
public class VendorController : AuthenticatedPortfolioControllerBase
{
    private readonly IVendorService _service;
    private readonly IVendorDispatchService _dispatch;

    public VendorController(IVendorService service, IVendorDispatchService dispatch)
    {
        _service = service;
        _dispatch = dispatch;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<VendorResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<VendorResponse>>> List([FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Vendor not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<VendorResponse>> Create([FromBody] CreateVendorRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(VendorResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorResponse>> Update(int id, [FromBody] UpdateVendorRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Vendor not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Vendor not found" });
    }

    /// <summary>Record a 1–5 star rating for the vendor and refresh its cached scorecard aggregates.</summary>
    [HttpPost("{id:int}/ratings")]
    [ProducesResponseType(typeof(VendorRatingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorRatingResponse>> Rate(int id, [FromBody] CreateVendorRatingRequest request, CancellationToken ct)
    {
        var created = await _dispatch.RateAsync(GetPortfolioId(), id, request, ct);
        return created == null
            ? NotFound(new { error = "Vendor not found" })
            : CreatedAtAction(nameof(Scorecard), new { id }, created);
    }

    /// <summary>Vendor performance scorecard: rating, jobs completed, and average DONE response time.</summary>
    [HttpGet("{id:int}/scorecard")]
    [ProducesResponseType(typeof(VendorScorecardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VendorScorecardResponse>> Scorecard(int id, CancellationToken ct)
    {
        var card = await _dispatch.GetScorecardAsync(GetPortfolioId(), id, ct);
        return card == null ? NotFound(new { error = "Vendor not found" }) : Ok(card);
    }
}
