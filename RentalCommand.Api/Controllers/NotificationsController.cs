using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Produces("application/json")]
public class NotificationsController : AuthenticatedPortfolioControllerBase
{
    private readonly INotificationService _notifications;
    private readonly INotificationSettingsService _settings;

    public NotificationsController(INotificationService notifications, INotificationSettingsService settings)
    {
        _notifications = notifications;
        _settings = settings;
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
    [Authorize(Roles = "Admin,Manager,Owner")]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<NotificationResponse>> Broadcast(
        [FromBody] CreateBroadcastNotificationRequest request,
        CancellationToken ct)
    {
        var created = await _notifications.CreateBroadcastAsync(GetPortfolioId(), request, ct);
        return Created($"/api/v1/notifications/{created.Id}", created);
    }

    [HttpGet("email")]
    [ProducesResponseType(typeof(NotificationEmailResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationEmailResponse>> GetNotificationEmail(CancellationToken ct)
    {
        return Ok(await _notifications.GetNotificationEmailAsync(GetPortfolioId(), ct));
    }

    [HttpPut("email")]
    [ProducesResponseType(typeof(NotificationEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationEmailResponse>> SetNotificationEmail(
        [FromBody] SetNotificationEmailRequest request,
        CancellationToken ct)
    {
        var response = await _notifications.SetNotificationEmailAsync(GetPortfolioId(), request.Email, ct);
        return response is null ? NotFound(new { error = "Portfolio not found" }) : Ok(response);
    }

    [HttpGet("settings")]
    [ProducesResponseType(typeof(NotificationSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationSettingsResponse>> GetSettings(CancellationToken ct)
    {
        return Ok(await _settings.GetAdminAsync(GetPortfolioId(), ct));
    }

    [HttpPut("settings")]
    [ProducesResponseType(typeof(NotificationSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationSettingsResponse>> SetSettings(
        [FromBody] UpdateNotificationSettingsRequest request,
        CancellationToken ct)
    {
        return Ok(await _settings.UpdateAsync(GetPortfolioId(), request, ct));
    }
}
