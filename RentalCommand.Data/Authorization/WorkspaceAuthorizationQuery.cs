using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Reusable authorization query primitive for property records. The correlated EXISTS remains part
/// of the caller's IQueryable so authorization runs before projection, count, sort, and paging; no
/// allowed-ID collection is ever materialized.
/// </summary>
public static class WorkspaceAuthorizationQuery
{
    public static IQueryable<Property> WhereAuthorized(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        ActiveAccessContext accessContext,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        var effectiveContexts = db.WorkspaceAccessContexts.AsNoTracking().WhereEffective();
        var effectiveMemberships = db.WorkspaceMemberships.AsNoTracking().WhereEffective(utcNow);
        var effectiveAssignments = db.MembershipRoleAssignments.AsNoTracking().WhereEffective(utcNow);

        return properties.Where(property =>
            property.PortfolioId == accessContext.PortfolioId &&
            effectiveAssignments.Any(assignment =>
                assignment.WorkspaceMembershipId == accessContext.WorkspaceMembershipId &&
                assignment.PortfolioId == property.PortfolioId &&
                effectiveMemberships.Any(membership =>
                    membership.Id == assignment.WorkspaceMembershipId &&
                    membership.AccessContextId == accessContext.AccessContextId &&
                    membership.PortfolioId == property.PortfolioId) &&
                effectiveContexts.Any(context =>
                    context.Id == accessContext.AccessContextId &&
                    context.UserId == accessContext.UserId &&
                    context.PortfolioId == property.PortfolioId &&
                    context.AccessRevision == accessContext.AccessRevision) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property) &&
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                  assignment.SelectedProperties.Any(scope =>
                      scope.PropertyId == property.Id &&
                      scope.PortfolioId == property.PortfolioId)))));
    }
}
