using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// AI-powered endpoints for the caller's portfolio. Scope comes from the JWT <c>portfolioId</c>
/// claim. Phase 3: Daily Briefing. Phase 3+: Portfolio Q&amp;A moat.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public class AiController : AuthenticatedPortfolioControllerBase
{
    private readonly IDailyBriefingService _briefing;

    public AiController(IDailyBriefingService briefing) => _briefing = briefing;

    /// <summary>Returns today's prioritized briefing for the portfolio.</summary>
    [HttpGet("briefing")]
    [ProducesResponseType(typeof(BriefingResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BriefingResponse>> Briefing(CancellationToken ct)
        => Ok(await _briefing.ComposeAsync(GetPortfolioId(), ct));
}
