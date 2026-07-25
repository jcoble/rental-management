using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Hubs;

/// <summary>
/// Realtime data-change channel. Each connection joins only the group for its current canonical
/// session and access revision. Publishers resolve authorized session groups from the database for
/// every invalidation, so relationship-only contexts, out-of-scope properties, stale revisions, and
/// revoked sessions cannot inherit a workspace-wide fanout group. JWT claims identify the session
/// coordinates only; they never directly select realtime groups.
/// </summary>
[Authorize]
public class DataUpdateHub : Hub
{
    private readonly IActiveAccessContextResolver _accessContextResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DataUpdateHub> _logger;

    public DataUpdateHub(
        IActiveAccessContextResolver accessContextResolver,
        TimeProvider timeProvider,
        ILogger<DataUpdateHub> logger)
    {
        _accessContextResolver = accessContextResolver;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var presented = GetPresentedAccessContext();
        ActiveAccessContext current;
        try
        {
            current = await _accessContextResolver.ResolveAsync(
                presented.SessionId,
                presented.UserId,
                presented.AccessContextId,
                presented.AccessRevision,
                _timeProvider.GetUtcNow().UtcDateTime,
                Context.ConnectionAborted);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "Rejected data update hub connection {ConnectionId} for inactive or stale access context {AccessContextId}",
                Context.ConnectionId,
                presented.AccessContextId);
            throw new HubException("The validated access context is unavailable.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            SessionRevisionGroup(current.SessionId, current.AccessRevision));
        _logger.LogInformation(
            "User {UserId} connected to data update hub with session {SessionId} revision {AccessRevision}",
            current.UserId,
            current.SessionId,
            current.AccessRevision);

        await base.OnConnectedAsync();
    }

    internal static string SessionRevisionGroup(Guid sessionId, long accessRevision) =>
        $"access-session-{sessionId:N}-revision-{accessRevision}";

    private ActiveAccessContext GetPresentedAccessContext()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is not null &&
            httpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
            value is ActiveAccessContext
            {
                SessionId: var sessionId,
                UserId: > 0,
                AccessContextId: > 0,
                PortfolioId: > 0,
                AccessRevision: > 0,
            } accessContext &&
            sessionId != Guid.Empty)
        {
            return accessContext;
        }

        _logger.LogWarning(
            "Rejected data update hub connection {ConnectionId} without a valid canonical access context",
            Context.ConnectionId);
        throw new HubException("The validated access context is unavailable.");
    }
}

/// <summary>
/// Safe cache-invalidation payload. Entity DTOs are deliberately not carried over SignalR; clients
/// refetch through the normally authorized REST query after receiving this hint.
/// </summary>
public class EntityUpdatePayload
{
    public string EntityType { get; set; } = null!;
    public int EntityId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>Payload broadcast on the <c>EntityDeleted</c> data-update event.</summary>
public class EntityDeletePayload
{
    public string EntityType { get; set; } = null!;
    public int EntityId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
