using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Notifications;

/// <summary>
/// Least-privilege notification recipient queries for system-originated events. These joins use the
/// TSK-670 workspace authority graph rather than legacy Identity roles: user -> active access context
/// -> active membership -> one active role assignment whose capability and property scope both match.
/// Keeping capability and scope on the same assignment prevents authority from being assembled across
/// unrelated assignments. Every method remains IQueryable so the caller executes one SQL query.
/// </summary>
public static class ScopedNotificationRecipientQuery
{
    /// <summary>
    /// Resolves the final internal recipients for a concrete event in one SQL statement. An exact
    /// property rule overrides the workspace-wide rule. Named recipients and current direct work
    /// responsibility are revalidated against the current access context, assignment capability,
    /// property scope, and access revision at event time. Only when neither branch produces a user
    /// does the configured, visible Workspace Administrator fallback apply.
    /// </summary>
    public static IQueryable<int> ForTeamTopic(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        TeamRoutingTopic topic,
        int? propertyId,
        int? workOrderId,
        DateTime utcNow)
    {
        var (capabilityKey, targetKind) = RoutingAuthority(topic);
        if (targetKind == CapabilityAuthorizationTargetKind.Property && propertyId is null)
            throw new ArgumentException("A property-scoped routing topic requires a property.", nameof(propertyId));
        if (workOrderId is not null && topic != TeamRoutingTopic.WorkOrders)
            throw new ArgumentException("Direct work responsibility applies only to work-order routing.", nameof(workOrderId));

        var rules = attempt.Persistence.Query<TeamRoutingRule>();
        var matchingRules = rules.Where(rule =>
            rule.PortfolioId == portfolioId
            && rule.Topic == topic
            && (rule.PropertyId == propertyId
                || rule.PropertyId == null && !rules.Any(exact =>
                    exact.PortfolioId == portfolioId
                    && exact.Topic == topic
                    && exact.PropertyId == propertyId)));

        var assignments =
            from context in attempt.Persistence.Query<WorkspaceAccessContext>()
            join membership in attempt.Persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where context.PortfolioId == portfolioId
                && context.AccessRevision > 0
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind)
                && (targetKind == CapabilityAuthorizationTargetKind.Workspace
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PortfolioId == portfolioId && scope.PropertyId == propertyId))
            select new
            {
                context.UserId,
                context.AccessRevision,
                MembershipId = membership.Id,
                AssignmentId = assignment.Id,
            };

        var explicitRecipients =
            from rule in matchingRules
            join recipient in attempt.Persistence.Query<TeamRoutingRuleRecipient>()
                on new { RuleId = rule.Id, rule.PortfolioId }
                equals new { RuleId = recipient.TeamRoutingRuleId, recipient.PortfolioId }
            join assignment in assignments on recipient.UserId equals assignment.UserId
            select recipient.UserId;

        var directAssignments =
            from context in attempt.Persistence.Query<WorkspaceAccessContext>()
            join membership in attempt.Persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where topic == TeamRoutingTopic.WorkOrders
                && workOrderId != null
                && context.PortfolioId == portfolioId
                && context.AccessRevision > 0
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkRead
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.WorkOrder)
            select new
            {
                context.UserId,
                MembershipId = membership.Id,
                AssignmentId = assignment.Id,
            };

        var directRecipients =
            from assignment in directAssignments
            join responsibility in attempt.Persistence.Query<WorkOrderResponsibility>()
                on new
                {
                    assignment.MembershipId,
                    assignment.AssignmentId,
                    PortfolioId = portfolioId,
                }
                equals new
                {
                    MembershipId = responsibility.WorkspaceMembershipId,
                    AssignmentId = responsibility.MembershipRoleAssignmentId,
                    responsibility.PortfolioId,
                }
            where responsibility.WorkOrderId == workOrderId
                && responsibility.PropertyId == propertyId
                && responsibility.EffectiveFromUtc <= utcNow
                && (responsibility.EffectiveToUtc == null || responsibility.EffectiveToUtc > utcNow)
            select assignment.UserId;

        var resolved = explicitRecipients.Union(directRecipients);
        var administratorFallback =
            from rule in matchingRules
            from assignment in assignments
            join roleAssignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on assignment.AssignmentId equals roleAssignment.Id
            where rule.UseWorkspaceAdministratorFallback
                && !resolved.Any()
                && roleAssignment.RoleProfile!.Key == RoleProfileKeys.WorkspaceAdministrator
            select assignment.UserId;

        return resolved.Union(administratorFallback)
            .Distinct()
            .TagWith("TSK-668 event-time team routing with direct responsibility and visible administrator fallback");
    }

    public static IQueryable<int> ForWorkspaceMembership(
        RentalCommandDbContext db,
        int portfolioId,
        DateTime utcNow)
    {
        return (
            from context in db.WorkspaceAccessContexts.AsNoTracking()
            join membership in db.WorkspaceMemberships.AsNoTracking()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            where context.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
            select context.UserId)
            .Distinct()
            .TagWith("ScopedNotificationRecipients: active workspace membership");
    }

    /// <summary>
    /// Resolves a tenant-originated event through the current canonical relationship property and
    /// the saved Team routing rule. The relationship, role capability, property scope, access
    /// revision, exact-property override, and administrator fallback remain inside one SQL query.
    /// </summary>
    public static IQueryable<int> ForTenantTeamTopic(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        int tenantId,
        TeamRoutingTopic topic,
        DateTime utcNow)
    {
        var (capabilityKey, targetKind) = RoutingAuthority(topic);
        if (targetKind != CapabilityAuthorizationTargetKind.Property)
            throw new ArgumentException("Tenant relationship routing requires a property-scoped topic.", nameof(topic));

        var effectiveProperties =
            from party in attempt.Persistence.Query<LeaseManagementParty>()
            join lifecycle in attempt.Persistence.Query<LeaseManagementLifecycleProjection>()
                on new { party.LeaseManagementId, party.PortfolioId }
                equals new { lifecycle.LeaseManagementId, lifecycle.PortfolioId }
            where party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && party.EffectiveFrom <= lifecycle.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
                && lifecycle.CurrentAgreementId != null
                && lifecycle.TenantAccountId != null
                && !lifecycle.HasReconciliationException
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && attempt.Persistence.Query<LeaseAgreementStatusProjection>().Any(agreement =>
                    agreement.PortfolioId == portfolioId
                    && agreement.LeaseManagementId == lifecycle.LeaseManagementId
                    && agreement.AgreementId == lifecycle.CurrentAgreementId
                    && agreement.IsGoverning)
            select lifecycle.PropertyId;

        var rules = attempt.Persistence.Query<TeamRoutingRule>();
        var exactRules = rules.Where(rule => rule.PortfolioId == portfolioId
            && rule.Topic == topic
            && rule.PropertyId != null
            && effectiveProperties.Contains(rule.PropertyId.Value));
        var matchingRules = exactRules.Concat(rules.Where(rule => rule.PortfolioId == portfolioId
            && rule.Topic == topic
            && rule.PropertyId == null
            && !exactRules.Any()));

        var assignments =
            from context in attempt.Persistence.Query<WorkspaceAccessContext>()
            join membership in attempt.Persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where context.PortfolioId == portfolioId
                && context.AccessRevision > 0
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind)
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope => scope.PortfolioId == portfolioId
                            && effectiveProperties.Contains(scope.PropertyId)))
            select new { context.UserId, AssignmentId = assignment.Id };

        var explicitRecipients =
            from rule in matchingRules
            join recipient in attempt.Persistence.Query<TeamRoutingRuleRecipient>()
                on new { RuleId = rule.Id, rule.PortfolioId }
                equals new { RuleId = recipient.TeamRoutingRuleId, recipient.PortfolioId }
            join assignment in assignments on recipient.UserId equals assignment.UserId
            select recipient.UserId;

        var administratorFallback =
            from rule in matchingRules
            from assignment in assignments
            join roleAssignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on assignment.AssignmentId equals roleAssignment.Id
            where rule.UseWorkspaceAdministratorFallback
                && !explicitRecipients.Any()
                && roleAssignment.RoleProfile!.Key == RoleProfileKeys.WorkspaceAdministrator
            select assignment.UserId;

        return explicitRecipients.Union(administratorFallback)
            .Distinct()
            .TagWith("TSK-668 tenant relationship Team routing with exact property and administrator fallback");
    }

    private static (string CapabilityKey, CapabilityAuthorizationTargetKind TargetKind) RoutingAuthority(
        TeamRoutingTopic topic) => topic switch
        {
            TeamRoutingTopic.RentAndMoney =>
                (CapabilityKeys.MoneyBalancesRead, CapabilityAuthorizationTargetKind.Property),
            TeamRoutingTopic.ApplicationsAndLeasing =>
                (CapabilityKeys.LeasingApplicationsManage, CapabilityAuthorizationTargetKind.Property),
            TeamRoutingTopic.WorkOrders =>
                (CapabilityKeys.WorkRead, CapabilityAuthorizationTargetKind.Property),
            TeamRoutingTopic.OwnerStatementsAndDecisions =>
                (CapabilityKeys.MoneyOwnerReportsRead, CapabilityAuthorizationTargetKind.Property),
            TeamRoutingTopic.AccountAndSecurity =>
                (CapabilityKeys.SecurityManage, CapabilityAuthorizationTargetKind.Workspace),
            TeamRoutingTopic.MorningBriefing =>
                (CapabilityKeys.RentalsRead, CapabilityAuthorizationTargetKind.Property),
            _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown Team routing topic."),
        };
}
