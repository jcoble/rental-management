using System.Security.Claims;
using RentalCommand.Core.Authorization;

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
        TimeProvider timeProvider)
    {
        var principal = httpContext.User;
        var hasAnyCoordinate = principal.HasClaim(claim =>
            claim.Type is "sid" or "ctx" or "ar");

        if (principal.Identity?.IsAuthenticated == true && hasAnyCoordinate)
        {
            if (!Guid.TryParse(principal.FindFirstValue("sid"), out var sessionId) ||
                !int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
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
                    timeProvider.GetUtcNow().UtcDateTime,
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

        await _next(httpContext);
    }
}
