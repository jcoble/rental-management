using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Controllers;

/// <summary>Portfolio-scoped AI assistant actions backed by the configured LLM provider.</summary>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public class AiController : AuthenticatedPortfolioControllerBase
{
    private readonly ILlmProvider _llm;

    public AiController(ILlmProvider llm)
    {
        _llm = llm;
    }

    [HttpPost("chat")]
    [ProducesResponseType(typeof(AiChatResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
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
