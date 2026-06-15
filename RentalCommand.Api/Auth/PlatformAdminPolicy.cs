using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Auth;

/// <summary>
/// Platform super-admin authorization policy (IA Wave 1, F6 / TSK-212).
///
/// Operator-only endpoints (Engine Health, and any future platform/diagnostic surface) must NOT
/// be reachable by ordinary landlord <c>Admin</c>s — every landlord holds the Admin role on their
/// OWN portfolio. Rental Command deliberately has no super-admin <em>role</em> (that would mean a
/// migration + seeding + assignment UI), so platform access is gated by a config email allowlist
/// instead: a caller is a platform admin iff their token's email claim is on
/// <see cref="PlatformAdminOptions.Emails"/>.
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
    /// True iff the principal carries an email claim that is on <paramref name="allowlist"/>.
    /// Checks both the standard <see cref="ClaimTypes.Email"/> URI claim and the short "email"
    /// claim (JWTs minted with the short name). An empty allowlist always returns false.
    /// </summary>
    public static bool IsPlatformAdmin(ClaimsPrincipal user, IReadOnlySet<string> allowlist)
    {
        if (allowlist.Count == 0)
        {
            return false;
        }

        var email = user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value;

        return email is not null && allowlist.Contains(email);
    }

    /// <summary>
    /// Registers the <see cref="Name"/> policy against a fixed allowlist. The allowlist is captured
    /// once at startup (config is not hot-reloaded for this gate); pass the result of
    /// <see cref="BuildAllowlist"/>.
    /// </summary>
    public static void Register(AuthorizationOptions options, IReadOnlySet<string> allowlist)
    {
        options.AddPolicy(Name, policy =>
            policy.RequireAssertion(ctx => IsPlatformAdmin(ctx.User, allowlist)));
    }
}
