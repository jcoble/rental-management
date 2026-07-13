using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Base for staff-facing workspace management controllers. Admission is reconstructed from the
/// selected canonical access context and its effective capability-bearing Team assignment; legacy
/// Identity roles and JWT role claims are not accepted. Concrete endpoints remain responsible for
/// their typed capability and resource-scope authorization.
/// </summary>
[Authorize(Policy = CanonicalManagementPolicy.Name)]
public abstract class ManagementControllerBase : AuthenticatedPortfolioControllerBase
{
    protected bool TryReadAccessContext(out LeaseManagementReadContext access)
    {
        access = default;
        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)
            || !int.TryParse(User.FindFirstValue("ctx"), out var accessContextId)
            || !long.TryParse(User.FindFirstValue("ar"), out var accessRevision))
        {
            return false;
        }

        access = new LeaseManagementReadContext(
            GetPortfolioId(), GetUserId(), sessionId, accessContextId, accessRevision);
        return true;
    }
}
