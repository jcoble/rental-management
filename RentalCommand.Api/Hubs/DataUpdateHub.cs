using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RentalCommand.Api.Hubs;

/// <summary>
/// Realtime data-change channel. On connect each client joins its <c>user-{userId}</c> and
/// <c>portfolio-{portfolioId}</c> groups (derived from JWT claims). The server broadcasts
/// <c>EntityUpdated</c>/<c>EntityDeleted</c> events to the portfolio group so the frontend can
/// invalidate cached queries. Requires auth; websocket transports supply the JWT via the
/// <c>access_token</c> query string (wired in Program.cs JwtBearerEvents).
/// </summary>
[Authorize]
public class DataUpdateHub : Hub
{
    private readonly ILogger<DataUpdateHub> _logger;

    public DataUpdateHub(ILogger<DataUpdateHub> logger)
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
                "User {UserId} connected to data update hub for portfolio {PortfolioId}",
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

/// <summary>Payload broadcast on the <c>EntityUpdated</c> data-update event.</summary>
public class EntityUpdatePayload
{
    public string EntityType { get; set; } = null!;
    public int EntityId { get; set; }
    public object? Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Payload broadcast on the <c>EntityDeleted</c> data-update event.</summary>
public class EntityDeletePayload
{
    public string EntityType { get; set; } = null!;
    public int EntityId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
