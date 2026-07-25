using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;

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
        var active = GetActiveAccessContext();
        var scope = new WorkspaceReadScope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        var experience = (NavigationExperience)(
            active.LastAuthorizedExperience ?? active.DefaultExperience ?? WorkspaceExperience.Management);
        var items = await _notifications.ListAsync(scope, experience, unreadOnly, skip, take, ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(NotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationResponse>> Get(int id, CancellationToken ct)
    {
        var active = GetActiveAccessContext();
        var scope = new WorkspaceReadScope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        var experience = (NavigationExperience)(
            active.LastAuthorizedExperience ?? active.DefaultExperience ?? WorkspaceExperience.Management);
        var item = await _notifications.GetAsync(scope, experience, id, ct);
        return item is null
            ? NotFound(new { error = "Notification not found" })
            : Ok(item);
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

    private BadRequestObjectResult InvalidKey() =>
        BadRequest(new { error = "Idempotency-Key is required and must be at most 128 characters." });
}
