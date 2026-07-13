using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

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
    protected WorkspaceReadScope GetWorkspaceReadScope()
    {
        var active = GetActiveAccessContext();
        return new WorkspaceReadScope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
    }

    protected bool TryReadWorkspaceScope(out WorkspaceReadScope scope)
    {
        scope = default;
        if (!TryGetActiveAccessContext(out var active))
        {
            return false;
        }

        scope = new WorkspaceReadScope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        return true;
    }

    protected bool TryReadAccessContext(out LeaseManagementReadContext access)
    {
        access = default;
        if (!TryGetActiveAccessContext(out var active))
        {
            return false;
        }

        access = new LeaseManagementReadContext(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        return true;
    }

    /// <summary>
    /// Makes an exact typed resource decision. This helper deliberately resolves scoped services
    /// from the request because controller base classes do not participate in constructor injection;
    /// the evaluator still executes the capability-and-scope proof in PostgreSQL.
    /// </summary>
    protected Task<bool> HasCapabilityAsync(
        string capabilityKey,
        WorkspaceAuthorizationTarget target,
        CancellationToken cancellationToken = default)
    {
        var active = GetActiveAccessContext();
        if (target.PortfolioId != active.PortfolioId)
        {
            return Task.FromResult(false);
        }

        var evaluator = HttpContext.RequestServices.GetRequiredService<IWorkspaceAuthorizationEvaluator>();
        var timeProvider = HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        return evaluator.HasCapabilityAsync(
            active,
            capabilityKey,
            target,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
    }
}
