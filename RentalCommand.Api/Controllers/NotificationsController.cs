using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Produces("application/json")]
public class NotificationsController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<NotificationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> List(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var items = await _notifications.ListAsync(GetPortfolioId(), GetUserId(), unreadOnly, skip, take, ct);
        return Ok(items);
    }

    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnreadCountResponse>> UnreadCount(CancellationToken ct)
    {
        var count = await _notifications.GetUnreadCountAsync(GetPortfolioId(), GetUserId(), ct);
        return Ok(new UnreadCountResponse(count));
    }

    [HttpPost("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsRead(int id, CancellationToken ct)
    {
        var found = await _notifications.MarkAsReadAsync(GetPortfolioId(), GetUserId(), id, ct);
        return found ? NoContent() : NotFound(new { error = "Notification not found" });
    }

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct)
    {
        await _notifications.MarkAllAsReadAsync(GetPortfolioId(), GetUserId(), ct);
        return NoContent();
    }

    [HttpPost("broadcast")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<NotificationResponse>> Broadcast(
        [FromBody] CreateBroadcastNotificationRequest request,
        CancellationToken ct)
    {
        var created = await _notifications.CreateBroadcastAsync(GetPortfolioId(), request, ct);
        return Created($"/api/v1/notifications/{created.Id}", created);
    }

}
