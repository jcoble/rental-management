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
    /// <summary>
    /// Applies the current-session, access-revision, capability, and selected-property predicates
    /// as a correlated EXISTS inside the caller's property query. This is the list/report primitive;
    /// it does not materialize allowed property ids.
    /// </summary>
    public static IQueryable<Property> WhereAuthorized(
        this IQueryable<Property> properties,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return properties.Where(property =>
            property.PortfolioId == scope.PortfolioId &&
            db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == scope.SessionId &&
                session.UserId == scope.UserId &&
                session.ActiveAccessContextId == scope.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.Id == scope.AccessContextId &&
                session.ActiveAccessContext.UserId == scope.UserId &&
                session.ActiveAccessContext.PortfolioId == scope.PortfolioId &&
                session.ActiveAccessContext.AccessRevision == scope.AccessRevision &&
                session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active &&
                session.ActiveAccessContext.SuspendedAtUtc == null &&
                session.ActiveAccessContext.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership != null &&
                session.ActiveAccessContext.Membership.PortfolioId == property.PortfolioId &&
                session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active &&
                session.ActiveAccessContext.Membership.SuspendedAtUtc == null &&
                session.ActiveAccessContext.Membership.RevokedAtUtc == null &&
                session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow &&
                (session.ActiveAccessContext.Membership.EffectiveToUtc == null ||
                 session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow) &&
                session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == property.PortfolioId &&
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null &&
                    assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= utcNow &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                    assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                        profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                        profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PortfolioId == property.PortfolioId &&
                          selected.PropertyId == property.Id))))));
    }

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
