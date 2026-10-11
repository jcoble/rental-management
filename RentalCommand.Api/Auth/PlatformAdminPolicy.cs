using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Configuration;
using RentalCommand.Data;

namespace RentalCommand.Api.Auth;

/// <summary>
/// Platform super-admin authorization policy (IA Wave 1, F6 / TSK-212).
///
/// Operator-only endpoints (Engine Health, and any future platform/diagnostic surface) are gated by
/// a configured email allowlist. The bearer contains only canonical identity coordinates; the
/// authenticated numeric subject is resolved to the user's current database email before the
/// allowlist is evaluated.
///
/// <para>
/// Fails closed: when the allowlist is empty, nobody passes. The single source of truth lives here
/// (not inline in Program.cs) so the gate and its security test cannot drift apart.
/// </para>
/// </summary>
public static class PlatformAdminPolicy
{
    /// <summary>Policy name referenced by <c>[Authorize(Policy = PlatformAdminPolicy.Name)]</c>.</summary>
    public const string Name = "PlatformAdmin";

    /// <summary>
    /// Builds the case-insensitive allowlist set from bound options, trimming blanks. An empty or
    /// null <paramref name="options"/> yields an empty set (fail closed — no one is a platform admin).
    /// </summary>
    public static HashSet<string> BuildAllowlist(PlatformAdminOptions? options)
    {
        var emails = options?.Emails ?? Array.Empty<string>();
        return emails
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrEmpty(e))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the <see cref="Name"/> policy against a fixed allowlist. The allowlist is captured
    /// once at startup (config is not hot-reloaded for this gate); pass the result of
    /// <see cref="BuildAllowlist"/>.
    /// </summary>
    public static void Register(AuthorizationOptions options, IReadOnlySet<string> allowlist)
    {
        options.AddPolicy(Name, policy =>
            policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformAdminRequirement(allowlist)));
    }
}

public sealed record PlatformAdminRequirement(
    IReadOnlySet<string> Allowlist) : IAuthorizationRequirement;

/// <summary>
/// Resolves the canonical JWT subject to the current ApplicationUser email in one database query,
/// then evaluates the configured platform-operator allowlist. Email and role claims are ignored.
/// </summary>
public sealed class PlatformAdminAuthorizationHandler
    : AuthorizationHandler<PlatformAdminRequirement>
{
    private readonly RentalCommandDbContext _db;

    public PlatformAdminAuthorizationHandler(RentalCommandDbContext db) => _db = db;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            requirement.Allowlist.Count == 0 ||
            !TryReadCanonicalUserId(context.User, out var userId))
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext httpContext
            ? httpContext.RequestAborted
            : CancellationToken.None;
        var email = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (email is not null && requirement.Allowlist.Contains(email))
        {
            context.Succeed(requirement);
        }
    }

    internal static bool TryReadCanonicalUserId(ClaimsPrincipal principal, out int userId)
    {
        // JwtBearer may expose `sub` either as NameIdentifier or as the raw registered claim name
        // depending on framework mapping defaults. Accept both shapes.
        var subject = principal.FindSubjectValue();
        return int.TryParse(subject, out userId) && userId > 0;
    }
}
