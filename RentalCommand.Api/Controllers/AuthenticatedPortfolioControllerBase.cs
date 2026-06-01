using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Base for controllers that operate within a single portfolio's tenant scope. Reads the
/// portfolio and user identity from JWT claims (never from request parameters). The user id is an
/// int (matching <c>ApplicationUser</c>) encoded as the <c>sub</c>/<see cref="ClaimTypes.NameIdentifier"/> claim.
/// </summary>
[Authorize]
public abstract class AuthenticatedPortfolioControllerBase : ControllerBase
{
    /// <summary>
    /// Portfolio id from the <c>portfolioId</c> claim. Throws when the claim is missing or
    /// unparseable so a request with no portfolio scope can never run a tenant-scoped query with
    /// <c>portfolioId == 0</c> (which would read across tenants). Use <see cref="TryGetPortfolioId"/>
    /// for code paths that legitimately tolerate an unscoped caller.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The <c>portfolioId</c> claim is absent or invalid.</exception>
    protected int GetPortfolioId()
    {
        if (!TryGetPortfolioId(out var id))
        {
            throw new UnauthorizedAccessException("Missing portfolio context");
        }

        return id;
    }

    /// <summary>
    /// Attempts to read the <c>portfolioId</c> claim without throwing. Returns <c>false</c> (and
    /// <paramref name="portfolioId"/> = 0) when the caller has no portfolio scope.
    /// </summary>
    protected bool TryGetPortfolioId(out int portfolioId)
    {
        var claim = User.FindFirst("portfolioId");
        if (claim != null && int.TryParse(claim.Value, out portfolioId))
        {
            return true;
        }

        portfolioId = 0;
        return false;
    }

    /// <summary>Int user id parsed from the <c>sub</c>/NameIdentifier claim.</summary>
    protected int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !int.TryParse(claim.Value, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user context");
        }

        return userId;
    }

    protected IEnumerable<string> GetRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);
}
