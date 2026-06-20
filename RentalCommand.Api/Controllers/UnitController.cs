using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for units. Units are scoped through their owning property's portfolio (the caller's
/// <c>portfolioId</c> claim). List supports <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort</c>.
/// Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/units")]
[Produces("application/json")]
public class UnitController : AuthenticatedPortfolioControllerBase
{
    private readonly IUnitService _service;
    private readonly IUnitDashboardService _dashboard;

    public UnitController(IUnitService service, IUnitDashboardService dashboard)
    {
        _service = service;
        _dashboard = dashboard;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UnitResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnitResponse>>> List(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Unit not found" }) : Ok(item);
    }

    /// <summary>
    /// At-a-glance Unit Command Center aggregate (header chips, current lease/tenant, derived lifecycle
    /// stage + next-best-action, capped overview, recent timeline). Portfolio-scoped; 404 when the unit
    /// is not in the caller's portfolio. Per-tab heavy data loads separately via the filtered endpoints.
    /// </summary>
    [HttpGet("{id:int}/dashboard")]
    [ProducesResponseType(typeof(UnitDashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitDashboardResponse>> Dashboard(int id, CancellationToken ct)
    {
        var dashboard = await _dashboard.GetDashboardAsync(GetPortfolioId(), id, ct);
        return dashboard == null ? NotFound(new { error = "Unit not found" }) : Ok(dashboard);
    }

    /// <summary>
    /// The unit's full history (deep timeline tab): a bounded <c>AuditLog</c> union over the unit and its
    /// children, newest first, paged via <c>?skip&amp;take</c>. Out-of-scope units return an empty list.
    /// </summary>
    [HttpGet("{id:int}/timeline")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEntryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditEntryResponse>>> Timeline(
        int id, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _dashboard.GetTimelineAsync(GetPortfolioId(), id, query.NormalizedSkip, query.NormalizedTake, ct);
        return Ok(items);
    }

    [HttpPost]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Create([FromBody] CreateUnitRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Property not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Update(int id, [FromBody] UpdateUnitRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Unit not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Unit not found" });
    }
}
