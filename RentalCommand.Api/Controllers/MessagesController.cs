using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Landlord-facing message inbox. All operations are portfolio-scoped via the JWT <c>portfolioId</c>
/// claim. Tenants submit messages through the <see cref="PortalController"/>; landlords view, reply,
/// and manage status here.
/// </summary>
[ApiController]
[Route("api/v1/messages")]
[Produces("application/json")]
public class MessagesController : AuthenticatedPortfolioControllerBase
{
    private readonly IMessageService _service;

    public MessagesController(IMessageService service)
    {
        _service = service;
    }

    /// <summary>List all messages in the portfolio, newest first. Optional <c>?status=</c> filter (case-insensitive).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MessageResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MessageResponse>>> List(
        [FromQuery] string? status, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), status, ct);
        return Ok(items);
    }

    /// <summary>Fetch a single message by id.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Message not found" }) : Ok(item);
    }

    /// <summary>
    /// Landlord → tenant: send a new message over the chosen channels (Portal/Email/Sms).
    /// Returns 201 with the created message, or 404 when the tenant is not in this portfolio.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> Create([FromBody] CreateMessageRequest request, CancellationToken ct)
    {
        var result = await _service.CreateToTenantAsync(GetPortfolioId(), GetUserId(), request, ct);
        if (result == null)
        {
            return NotFound(new { error = "Tenant not found" });
        }

        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>Post a landlord reply. Optionally override status (defaults to InProgress).</summary>
    [HttpPost("{id:int}/reply")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> Reply(int id, [FromBody] ReplyMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reply))
        {
            return BadRequest(new { error = "Reply cannot be empty" });
        }

        var result = await _service.ReplyAsync(GetPortfolioId(), id, request, ct);
        return result == null ? NotFound(new { error = "Message not found" }) : Ok(result);
    }

    /// <summary>Update the status of a message (400 on unrecognised status value).</summary>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MessageResponse>> UpdateStatus(int id, [FromBody] UpdateMessageStatusRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _service.UpdateStatusAsync(GetPortfolioId(), id, request.Status, ct);
            return result == null ? NotFound(new { error = "Message not found" }) : Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
