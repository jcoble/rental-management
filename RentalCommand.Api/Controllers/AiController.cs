using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// AI-powered endpoints for the caller's portfolio. Scope comes from the JWT <c>portfolioId</c>
/// claim. Includes daily briefing, portfolio Q&amp;A, and a direct chat endpoint.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public class AiController : AuthenticatedPortfolioControllerBase
{
    private readonly IDailyBriefingService _briefing;
    private readonly IPortfolioQaService _qa;
    private readonly ILlmProvider _llm;

    public AiController(IDailyBriefingService briefing, IPortfolioQaService qa, ILlmProvider llm)
    {
        _briefing = briefing;
        _qa = qa;
        _llm = llm;
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

        var delivery = BuildDelivery(req);
        return Ok(await _qa.AskAsync(GetPortfolioId(), req.Question, req.History, delivery, ct));
    }

    /// <summary>
    /// Maps the request's delivery flags to resolved <see cref="QaDeliveryOptions"/>. The recipient
    /// is never client-supplied: email is the authenticated user's own claim email, and the phone is
    /// resolved server-side (the portfolio owner's number) inside the service. This keeps the feature
    /// to "send this answer to me" and prevents using the assistant as an email/SMS open relay.
    /// </summary>
    private QaDeliveryOptions BuildDelivery(AskRequest req)
    {
        if (!req.DeliverViaEmail && !req.DeliverViaSms)
            return QaDeliveryOptions.None;

        return new QaDeliveryOptions(req.DeliverViaEmail, req.DeliverViaSms, GetUserEmail());
    }

    /// <summary>The signed-in user's email from claims (mapped or raw), or null when absent.</summary>
    private string? GetUserEmail()
    {
        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
        return string.IsNullOrWhiteSpace(email) ? null : email;
    }

    /// <summary>Direct assistant chat for the in-app AI page.</summary>
    [HttpPost("chat")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AiChatResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest("Message is required.");
        if (request.Message.Length > 8000) return BadRequest("Message is too long (max 8000 characters).");

        var portfolioId = GetPortfolioId();
        var prompt = $"""
            You are Rental Command's assistant for a rental property management system.
            Help the signed-in operator with practical property-management, leasing, maintenance,
            accounting, scanning, and tenant-communication tasks.

            Portfolio id: {portfolioId}

            User request:
            {request.Message}
            """;

        var reply = await _llm.ChatAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(reply))
        {
            return Ok(new AiChatResponse
            {
                Reply = "The AI provider is not configured or returned an empty response. Check Assistant:ApiKey and Assistant:ModelId, then restart the API."
            });
        }

        return Ok(new AiChatResponse { Reply = reply });
    }
}
