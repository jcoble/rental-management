using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Data;

namespace RentalCommand.Api.Auth;

public static class CanonicalAccessContextHttpItem
{
    public const string Key = "RentalCommand.ActiveAccessContext";
}

/// <summary>
/// Resolves the compact JWT coordinates against current PostgreSQL authority once per request.
/// Controllers consume the resulting portfolio/user context; none reconstruct workspace authority
/// from removed role or portfolio claims.
/// </summary>
public sealed class CanonicalAccessContextMiddleware
{
    private readonly RequestDelegate _next;

    public CanonicalAccessContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext httpContext,
        IActiveAccessContextResolver resolver,
        IAuthSecurityClock securityClock,
        RentalCommandDbContext db)
    {
        var principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            if (!Guid.TryParse(principal.FindFirstValue("sid"), out var sessionId) ||
                !principal.TryReadSubjectUserId(out var userId) ||
                !int.TryParse(principal.FindFirstValue("ctx"), out var accessContextId) ||
                !long.TryParse(principal.FindFirstValue("ar"), out var accessRevision))
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            try
            {
                var active = await resolver.ResolveAsync(
                    sessionId,
                    userId,
                    accessContextId,
                    accessRevision,
                    securityClock.UtcNow(),
                    httpContext.RequestAborted);

                httpContext.Items[CanonicalAccessContextHttpItem.Key] = active;
            }
            catch (AccessContextUnavailableException)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            catch (StaleAccessRevisionException)
            {
                httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                httpContext.Response.Headers["X-Access-Envelope-Refresh"] = "required";
                return;
            }
        }

        if (principal.Identity?.IsAuthenticated != true ||
            IsLongLivedHubRequest(httpContext.Request.Path))
        {
            await _next(httpContext);
            return;
        }

        var connectionOpened = false;
        try
        {
            // Keep the scoped EF connection open after canonical resolution so the RLS
            // interceptor configures it once and every downstream query in this request
            // reuses the same validated PostgreSQL session coordinates.
            await db.Database.OpenConnectionAsync(httpContext.RequestAborted);
            connectionOpened = true;
            await _next(httpContext);
        }
        finally
        {
            if (connectionOpened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static bool IsLongLivedHubRequest(PathString path) =>
        path.StartsWithSegments("/api/v1/hubs");
}
