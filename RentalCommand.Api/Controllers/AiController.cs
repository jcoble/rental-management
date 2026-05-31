using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// AI-powered endpoints for the caller's portfolio. Scope comes from the JWT <c>portfolioId</c>
/// claim. Phase 3: Daily Briefing + Portfolio Q&amp;A.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public class AiController : AuthenticatedPortfolioControllerBase
{
    private readonly IDailyBriefingService _briefing;
    private readonly IPortfolioQaService _qa;

    public AiController(IDailyBriefingService briefing, IPortfolioQaService qa)
    {
        _briefing = briefing;
        _qa = qa;
    }

    /// <summary>Returns today's prioritized briefing for the portfolio.</summary>
    [HttpGet("briefing")]
    [ProducesResponseType(typeof(BriefingResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BriefingResponse>> Briefing(CancellationToken ct)
        => Ok(await _briefing.ComposeAsync(GetPortfolioId(), ct));

    /// <summary>
    /// Answers a natural-language question about the portfolio using live data via tool-calling.
    /// Supports multi-turn conversation via the optional <c>history</c> field.
    /// </summary>
    [HttpPost("ask")]
    [ProducesResponseType(typeof(AskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest("Question is required.");
        if (req.Question.Length > 4000) return BadRequest("Question is too long (max 4000 characters).");
        return Ok(await _qa.AskAsync(GetPortfolioId(), req.Question, req.History, ct));
    }
}
