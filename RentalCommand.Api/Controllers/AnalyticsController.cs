using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only portfolio analytics — KPIs and trends for the web dashboard.
/// All data is scoped to the caller's server-validated workspace context.
/// No create / update / delete operations exist here.
/// </summary>
[ApiController]
[Route("api/v1/analytics")]
[Produces("application/json")]
public class AnalyticsController : ManagementControllerBase
{
    private readonly IAnalyticsService _analytics;

    public AnalyticsController(IAnalyticsService analytics)
    {
        _analytics = analytics;
    }

    /// <summary>
    /// Full analytics overview for the caller's portfolio: occupancy, this-month rent collection,
    /// overdue payments, 12-month income/expense trend, lease expiry counts, open work order
    /// breakdown by priority, and total monthly recurring rent.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(AnalyticsOverview), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsOverview>> Overview(CancellationToken ct)
    {
        var overview = await _analytics.GetOverviewAsync(GetPortfolioId(), ct);
        return Ok(overview);
    }
}
