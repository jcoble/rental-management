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
    /// <summary>Portfolio id from the <c>portfolioId</c> claim, or 0 if unscoped.</summary>
    protected int GetPortfolioId()
    {
        var claim = User.FindFirst("portfolioId");
        return claim != null && int.TryParse(claim.Value, out var id) ? id : 0;
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
