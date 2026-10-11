using System.Security.Claims;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Auditing;

/// <summary>
/// HTTP-request-backed <see cref="ICurrentActor"/>. Resolves the authenticated user id + display
/// label from JWT claims and the client IP (honoring Traefik's <c>X-Forwarded-For</c>) so the audit
/// interceptor can attribute each row. Every member is null-safe when there is no current request
/// (e.g. background work running in the API process).
/// </summary>
public sealed class HttpCurrentActor : ICurrentActor
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentActor(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public int? UserId
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            return user != null && user.TryReadSubjectUserId(out var id) ? id : null;
        }
    }

    public string? ActorLabel
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            if (user is null)
            {
                return null;
            }

            return user.FindFirst("displayName")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("email")?.Value;
        }
    }

    public string? IpAddress
    {
        get
        {
            var context = _accessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            // Behind Traefik the real client IP is the first hop of X-Forwarded-For.
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                var firstHop = forwarded.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(firstHop))
                {
                    return firstHop;
                }
            }

            return context.Connection.RemoteIpAddress?.ToString();
        }
    }
}
