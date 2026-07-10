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

        return properties.Where(property =>
            property.PortfolioId == accessContext.PortfolioId &&
            db.MembershipRoleAssignments.Any(assignment =>
                assignment.WorkspaceMembershipId == accessContext.WorkspaceMembershipId &&
                assignment.PortfolioId == property.PortfolioId &&
                assignment.WorkspaceMembership!.AccessContextId == accessContext.AccessContextId &&
                assignment.WorkspaceMembership.AccessContext!.UserId == accessContext.UserId &&
                assignment.WorkspaceMembership.AccessContext.AccessRevision == accessContext.AccessRevision &&
                assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= utcNow &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey) &&
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                 (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                  assignment.SelectedProperties.Any(scope => scope.PropertyId == property.Id)))));
    }
}
