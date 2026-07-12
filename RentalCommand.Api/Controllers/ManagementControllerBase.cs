using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Auth;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Base for staff-facing workspace management controllers. Admission is reconstructed from the
/// selected canonical access context and its effective capability-bearing Team assignment; legacy
/// Identity roles and JWT role claims are not accepted. Concrete endpoints remain responsible for
/// their typed capability and resource-scope authorization.
/// </summary>
[Authorize(Policy = CanonicalManagementPolicy.Name)]
public abstract class ManagementControllerBase : AuthenticatedPortfolioControllerBase;
