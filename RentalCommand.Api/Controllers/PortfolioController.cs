using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Portfolio access for the signed-in user's active, server-validated workspace context.
/// </summary>
[ApiController]
[Route("api/v1/portfolios")]
[Produces("application/json")]
public class PortfolioController : ManagementControllerBase
{
    private readonly IPortfolioService _service;
    private readonly IDashboardService _dashboard;

    public PortfolioController(IPortfolioService service, IDashboardService dashboard)
    {
        _service = service;
        _dashboard = dashboard;
    }

    /// <summary>List the portfolio selected by the caller's active workspace context.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PortfolioResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PortfolioResponse>>> List(CancellationToken ct)
    {
        var items = await _service.ListForUserAsync(GetPortfolioId(), ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortfolioResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Portfolio not found" }) : Ok(item);
    }

    /// <summary>
    /// Aggregated KPI rollup the web dashboard renders. The <paramref name="id"/> route segment exists to
    /// match the client URL but is ignored for scoping — data is always scoped to the caller's
    /// active workspace context.
    /// </summary>
    [HttpGet("{id:int}/dashboard")]
    [ProducesResponseType(typeof(DashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DashboardResponse>> Dashboard(int id, CancellationToken ct)
    {
        var dashboard = await _dashboard.GetDashboardAsync(GetWorkspaceReadScope(), ct);
        return dashboard == null ? NotFound(new { error = "Portfolio not found" }) : Ok(dashboard);
    }

    /// <summary>
    /// Server-shaped completion facts for the getting-started checklist. Scoped to the caller's active workspace
    /// so web/mobile do not need to download list pages just to count/sum them.
    /// </summary>
    [HttpGet("getting-started")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    [ProducesResponseType(typeof(GettingStartedSignalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GettingStartedSignalsResponse>> GettingStarted(CancellationToken ct)
    {
        var signals = await _service.GetGettingStartedSignalsAsync(GetPortfolioId(), ct);
        return signals == null ? NotFound(new { error = "Portfolio not found" }) : Ok(signals);
    }

    [HttpPatch("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    [ProducesResponseType(typeof(PortfolioResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortfolioResponse>> Update(int id, [FromBody] UpdatePortfolioRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Portfolio not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Portfolio not found" });
    }
}
