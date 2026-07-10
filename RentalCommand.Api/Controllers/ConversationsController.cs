using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Landlord-facing threaded messenger. Conversations are scoped by topic — a tenant can have multiple
/// threads, each with its own back-and-forth history. All operations are portfolio-scoped via the JWT
/// <c>portfolioId</c> claim. Tenants use the conversation endpoints on <see cref="PortalController"/>.
/// </summary>
[ApiController]
[Route("api/v1/conversations")]
[Produces("application/json")]
public class ConversationsController : ManagementControllerBase
{
    private readonly IConversationService _service;

    public ConversationsController(IConversationService service)
    {
        _service = service;
    }

    /// <summary>List the portfolio's conversations, most-recently-active first. Unread = landlord's.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConversationSummary>>> List(CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(ConversationListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ConversationListResponse>> ListPage([FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(ConversationUnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ConversationUnreadCountResponse>> UnreadCount(CancellationToken ct)
    {
        var count = await _service.GetUnreadCountAsync(GetPortfolioId(), ct);
        return Ok(new ConversationUnreadCountResponse(count));
    }

    /// <summary>Fetch a conversation with its full message history; resets the landlord's unread count.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Conversation not found" }) : Ok(item);
    }

    /// <summary>
    /// Open a new topic thread with a tenant and send the first message over the chosen channels.
    /// Returns 201 with the conversation detail, or 404 when the tenant is not in this portfolio.
    /// A 422 is returned when the Fair Housing review flags the copy and
    /// <c>acknowledgedFairHousingReview</c> is false; the body carries a <c>fairHousingConcerns</c> list.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConversationDetail>> Start(
        [FromBody] StartConversationRequest request, CancellationToken ct)
    {
        var result = await _service.StartAsync(
            GetPortfolioId(), request.TenantId, request.Subject, request.Body, request.Channels,
            request.OperationKey, request.AcknowledgedFairHousingReview, ct);

        return result == null
            ? NotFound(new { error = "Tenant not found" })
            : CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>Append a landlord message to an existing conversation and fan out over the chosen channels.</summary>
    [HttpPost("{id:int}/messages")]
    [ProducesResponseType(typeof(ConversationDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationDetail>> PostMessage(
        int id, [FromBody] PostMessageRequest request, CancellationToken ct)
    {
        var result = await _service.PostMessageAsync(
            GetPortfolioId(), id, request.Body, request.Channels, request.OperationKey, ct);
        return result == null ? NotFound(new { error = "Conversation not found" }) : Ok(result);
    }
}
