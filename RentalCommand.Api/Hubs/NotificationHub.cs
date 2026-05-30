using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RentalCommand.Api.Hubs;

/// <summary>
/// Realtime notification channel. On connect each client joins its <c>user-{userId}</c> and
/// <c>portfolio-{portfolioId}</c> groups (derived from JWT claims) so the server can target a
/// single user or every member of a portfolio. Requires auth; for websocket transports the JWT is
/// supplied via the <c>access_token</c> query string (wired in Program.cs JwtBearerEvents).
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        var portfolioId = GetPortfolioId();

        if (userId.HasValue)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
        }

        if (portfolioId.HasValue)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"portfolio-{portfolioId}");
            _logger.LogInformation(
                "User {UserId} connected to notification hub for portfolio {PortfolioId}",
                userId, portfolioId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        var portfolioId = GetPortfolioId();

        if (userId.HasValue)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user-{userId}");
        }

        if (portfolioId.HasValue)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"portfolio-{portfolioId}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    private int? GetUserId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && int.TryParse(claim.Value, out var userId) ? userId : null;
    }

    private int? GetPortfolioId()
    {
        var claim = Context.User?.FindFirst("portfolioId");
        return claim != null && int.TryParse(claim.Value, out var portfolioId) ? portfolioId : null;
    }
}

/// <summary>
/// Server-side helper for pushing notifications/alerts to connected clients via the notification hub.
/// </summary>
public interface INotificationHubService
{
    /// <summary>Send a notification message to every connection in the portfolio-{portfolioId} group.</summary>
    Task SendNotificationAsync(int portfolioId, object notification);

    /// <summary>Send a notification message to a single user's connections (user-{userId} group).</summary>
    Task SendNotificationToUserAsync(int userId, object notification);

    /// <summary>Send a severity-tagged alert to every connection in the portfolio-{portfolioId} group.</summary>
    Task SendAlertAsync(int portfolioId, string severity, string message);
}

/// <inheritdoc />
public class NotificationHubService : INotificationHubService
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<NotificationHubService> _logger;

    public NotificationHubService(
        IHubContext<NotificationHub> hubContext,
        ILogger<NotificationHubService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendNotificationAsync(int portfolioId, object notification)
    {
        try
        {
            await _hubContext.Clients
                .Group($"portfolio-{portfolioId}")
                .SendAsync("ReceiveNotification", notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification to portfolio {PortfolioId}", portfolioId);
        }
    }

    public async Task SendNotificationToUserAsync(int userId, object notification)
    {
        try
        {
            await _hubContext.Clients
                .Group($"user-{userId}")
                .SendAsync("ReceiveNotification", notification);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification to user {UserId}", userId);
        }
    }

    public async Task SendAlertAsync(int portfolioId, string severity, string message)
    {
        try
        {
            await _hubContext.Clients
                .Group($"portfolio-{portfolioId}")
                .SendAsync("ReceiveAlert", new { severity, message, timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert to portfolio {PortfolioId}", portfolioId);
        }
    }
}
