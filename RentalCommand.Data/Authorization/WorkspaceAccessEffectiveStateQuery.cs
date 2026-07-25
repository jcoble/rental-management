using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Shared SQL-translatable effective-state predicates. Authorization resolvers, evaluators, and
/// record queries compose these IQueryable filters so an Active label can never override a stored
/// suspension/revocation fact or an expired effective period.
/// </summary>
public static class WorkspaceAccessEffectiveStateQuery
{
    /// <summary>
    /// Limits login/session candidates to management-business contexts that still have an effective
    /// membership and at least one effective assignment. All predicates remain composable SQL; a
    /// membership label by itself never grants an authenticated workspace context.
    /// </summary>
    public static IQueryable<WorkspaceAccessContext> WhereEffectiveAccess(
        this IQueryable<WorkspaceAccessContext> contexts,
        IQueryable<WorkspaceMembership> memberships,
        IQueryable<MembershipRoleAssignment> assignments,
        IQueryable<OwnerUserAccess> ownerRelationships,
        IQueryable<EffectiveTenantAccessProjection> effectiveTenantRelationships,
        int userId,
        DateTime utcNow)
    {
        var effectiveMemberships = memberships.WhereEffective(utcNow);
        var effectiveAssignments = assignments.WhereEffective(utcNow);
        return contexts
            .WhereEffective()
            .Where(context =>
                (userId <= 0 || context.UserId == userId) &&
                (effectiveMemberships.Any(membership =>
                     membership.AccessContextId == context.Id &&
                     membership.PortfolioId == context.PortfolioId &&
                     effectiveAssignments.Any(assignment =>
                         assignment.WorkspaceMembershipId == membership.Id &&
                         assignment.PortfolioId == membership.PortfolioId)) ||
                 ownerRelationships.Any(access =>
                     access.AccessContextId == context.Id &&
                     access.ApplicationUserId == context.UserId &&
                     access.PortfolioId == context.PortfolioId &&
                     access.RevokedAtUtc == null &&
                     access.EffectiveFromUtc <= utcNow &&
                     (access.EffectiveToUtc == null || access.EffectiveToUtc > utcNow)) ||
                 effectiveTenantRelationships.Any(access =>
                     access.AccessContextId == context.Id &&
                     access.UserId == context.UserId &&
                     access.PortfolioId == context.PortfolioId)));
    }

    public static IQueryable<WorkspaceAccessContext> WhereEffective(
        this IQueryable<WorkspaceAccessContext> query) =>
        query.Where(context =>
            context.Status == WorkspaceAccessContextStatus.Active &&
            context.SuspendedAtUtc == null &&
            context.RevokedAtUtc == null);

    public static IQueryable<WorkspaceMembership> WhereEffective(
        this IQueryable<WorkspaceMembership> query,
        DateTime utcNow) =>
        query.Where(membership =>
            membership.Status == WorkspaceMembershipStatus.Active &&
            membership.SuspendedAtUtc == null &&
            membership.RevokedAtUtc == null &&
            membership.EffectiveFromUtc <= utcNow &&
            (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow));

    public static IQueryable<MembershipRoleAssignment> WhereEffective(
        this IQueryable<MembershipRoleAssignment> query,
        DateTime utcNow) =>
        query.Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= utcNow &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow));
}
