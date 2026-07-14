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
    public async Task<IActionResult> MarkAsRead(
        int id,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        var found = await _notifications.MarkAsReadAsync(GetMutationScope(), id, operationKey, ct);
        return found ? NoContent() : NotFound(new { error = "Notification not found" });
    }

    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllAsRead(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        await _notifications.MarkAllAsReadAsync(GetMutationScope(), operationKey, ct);
        return NoContent();
    }

    [HttpPost("broadcast")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<NotificationResponse>> Broadcast(
        [FromBody] CreateBroadcastNotificationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var operationKey)) return InvalidKey();
        var created = await _notifications.CreateBroadcastAsync(GetMutationScope(), request, operationKey, ct);
        return Created($"/api/v1/notifications/{created.Id}", created);
    }

    private WorkspaceReadScope GetMutationScope()
    {
        var active = GetActiveAccessContext();
        return new WorkspaceReadScope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
    }

    private static bool TryValidateIdempotencyKey(string? raw, out string key)
    {
        key = raw?.Trim() ?? string.Empty;
        return key.Length is > 0 and <= 128;
    }

    private BadRequestObjectResult InvalidKey() =>
        BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
}
