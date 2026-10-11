using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// AI-powered endpoints for the caller's portfolio. Scope comes from the server-validated workspace
/// context. Includes daily briefing, portfolio Q&amp;A, and a direct chat endpoint.
/// </summary>
[ApiController]
[Route("api/v1/ai")]
[Produces("application/json")]
public class AiController : ManagementControllerBase
{
    private readonly IDailyBriefingService _briefing;
    private readonly IPortfolioQaService _qa;
    private readonly IAssistantActionService _actions;
    private readonly IFairHousingReviewService _fairHousing;
    private readonly ILlmProvider _llm;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public AiController(
        IDailyBriefingService briefing,
        IPortfolioQaService qa,
        IAssistantActionService actions,
        IFairHousingReviewService fairHousing,
        ILlmProvider llm,
        RentalCommandDbContext db,
        TimeProvider timeProvider)
    {
        _briefing = briefing;
        _qa = qa;
        _actions = actions;
        _fairHousing = fairHousing;
        _llm = llm;
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <summary>Returns today's prioritized briefing for the portfolio.</summary>
    [HttpGet("briefing")]
    [ProducesResponseType(typeof(BriefingResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<BriefingResponse>> Briefing(CancellationToken ct)
    {
        var scope = GetWorkspaceReadScope();
        if (!await HasPropertyCapabilityAsync(scope, CapabilityKeys.ReportsRead, ct)) return Forbid();
        return Ok(await _briefing.ComposeAsync(scope, ct));
    }

    /// <summary>
    /// Answers a natural-language question about the portfolio using live data via tool-calling.
    /// Supports multi-turn conversation via the optional <c>history</c> field.
    /// </summary>
    [HttpPost("ask")]
    [ProducesResponseType(typeof(AskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AskResponse>> Ask(
        [FromBody] AskRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest(new { error = "Question is required." });
        if (req.Question.Length > 4000) return BadRequest(new { error = "Question is too long (max 4000 characters)." });
        var deliveryOperationId = NormalizeIdempotencyKey(idempotencyKey);
        if ((req.DeliverViaEmail || req.DeliverViaSms) && deliveryOperationId is null)
            return BadRequest(new { error = "A request key is required for Q&A delivery and cannot exceed 128 characters." });

        var scope = GetWorkspaceReadScope();
        if (!await HasPropertyCapabilityAsync(scope, CapabilityKeys.ReportsRead, ct)) return Forbid();
        var delivery = await BuildDeliveryAsync(req, ct);
        return Ok(await _qa.AskAsync(scope, req.Question, req.History, delivery, deliveryOperationId, ct));
    }

    /// <summary>
    /// Drafts a consequential assistant action without changing data. Execution is a separate call
    /// that requires write mode and explicit confirmation.
    /// </summary>
    [HttpPost("actions/draft")]
    [ProducesResponseType(typeof(AssistantActionDraftResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AssistantActionDraftResponse>> DraftAction(
        [FromBody] AssistantActionDraftRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Command)) return BadRequest(new { error = "Command is required." });
        if (req.Command.Length > 2000) return BadRequest(new { error = "Command is too long (max 2000 characters)." });

        var scope = GetWorkspaceReadScope();
        if (!await HasPropertyCapabilityAsync(scope, CapabilityKeys.MoneyExpensesManage, ct)) return Forbid();
        return Ok(await _actions.DraftAsync(scope, req, ct));
    }

    /// <summary>
    /// Executes a reviewed assistant action. This endpoint deliberately requires both explicit write
    /// mode and confirmation on every request; a previously-generated draft is never enough by itself.
    /// </summary>
    [HttpPost("actions/execute")]
    [ProducesResponseType(typeof(AssistantActionExecuteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AssistantActionExecuteResponse>> ExecuteAction(
        [FromBody] AssistantActionExecuteRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        if (req.Draft is null) return BadRequest(new { error = "Draft is required." });
        if (req.Draft.Expense?.PropertyId is not int propertyId) return Forbid();

        var scope = GetWorkspaceReadScope();
        if (!await HasCapabilityAsync(
                CapabilityKeys.MoneyExpensesManage,
                new PropertyCapabilityAuthorizationTarget(scope.PortfolioId, propertyId),
                ct))
        {
            return Forbid();
        }

        return Ok(await _actions.ExecuteAsync(scope, req, idempotencyKey, ct));
    }

    /// <summary>
    /// Maps the request's delivery flags to resolved <see cref="QaDeliveryOptions"/>. The recipient
    /// is never client-supplied: email and phone are read from the authenticated user's current database
    /// account. This keeps the feature
    /// to "send this answer to me" and prevents using the assistant as an email/SMS open relay.
    /// </summary>
    private async Task<QaDeliveryOptions> BuildDeliveryAsync(AskRequest req, CancellationToken ct)
    {
        if (!req.DeliverViaEmail && !req.DeliverViaSms)
            return QaDeliveryOptions.None;

        var recipient = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == GetUserId())
            .Select(user => new { user.Email, user.PhoneNumber })
            .SingleOrDefaultAsync(ct);
        return new QaDeliveryOptions(
            req.DeliverViaEmail,
            req.DeliverViaSms,
            req.DeliverViaEmail && !string.IsNullOrWhiteSpace(recipient?.Email) ? recipient.Email : null,
            req.DeliverViaSms && !string.IsNullOrWhiteSpace(recipient?.PhoneNumber) ? recipient.PhoneNumber : null);
    }

    private static string? NormalizeIdempotencyKey(string? idempotencyKey)
    {
        var value = idempotencyKey?.Trim();
        return value is { Length: > 0 and <= 128 } ? value : null;
    }

    /// <summary>
    /// Property-scoped capabilities remain usable for an AllProperties assignment before the first
    /// Property exists. Checking only the authorized Property query incorrectly forbids a brand-new
    /// workspace administrator from loading the empty Daily Briefing or using setup-adjacent AI.
    /// SelectedProperties assignments still require an actual authorized Property.
    /// </summary>
    private async Task<bool> HasPropertyCapabilityAsync(
        WorkspaceReadScope scope,
        string capabilityKey,
        CancellationToken ct)
    {
        if (await HasCapabilityAsync(
                capabilityKey,
                new PortfolioWidePropertyCapabilityAuthorizationTarget(scope.PortfolioId),
                ct))
        {
            return true;
        }

        return await _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, capabilityKey, _timeProvider.GetUtcNow().UtcDateTime)
            .AnyAsync(ct);
    }

    /// <summary>Direct assistant chat for the in-app AI page.</summary>
    [HttpPost("chat")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AiChatResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AiChatResponse>> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest(new { error = "Message is required." });
        if (request.Message.Length > 8000) return BadRequest(new { error = "Message is too long (max 8000 characters)." });

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

    /// <summary>
    /// Reviews landlord-written copy (a tenant notice or listing) for Fair Housing Act issues and
    /// suggests a compliant rewrite. When no AI key is configured the result has <c>reviewed=false</c>
    /// and is never reported as compliant — callers must surface "AI review unavailable".
    /// </summary>
    [HttpPost("fair-housing-check")]
    [ProducesResponseType(typeof(FairHousingReviewResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FairHousingReviewResult>> FairHousingCheck(
        [FromBody] FairHousingCheckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) return BadRequest(new { error = "Text is required." });
        if (request.Text.Length > 8000) return BadRequest(new { error = "Text is too long (max 8000 characters)." });

        return Ok(await _fairHousing.ReviewAsync(request.Text, ct));
    }
}
