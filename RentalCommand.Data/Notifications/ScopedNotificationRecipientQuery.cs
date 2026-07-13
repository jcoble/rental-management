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
internal static class ScopedNotificationRecipientQuery
{
    public static IQueryable<int> ForProperty(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        int propertyId,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        return (
            from user in attempt.Persistence.Query<ApplicationUser>()
            join context in attempt.Persistence.Query<WorkspaceAccessContext>()
                on new { UserId = user.Id, PortfolioId = user.PortfolioId!.Value }
                equals new { context.UserId, context.PortfolioId }
            join membership in attempt.Persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where user.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property)
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PortfolioId == portfolioId && scope.PropertyId == propertyId)))
            select user.Id)
            .Distinct()
            .TagWith("ScopedNotificationRecipients: property capability and assignment scope");
    }

    public static IQueryable<int> ForTenantRelationship(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        int tenantId,
        string capabilityKey,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);

        // The relationship boundary is an effective party in a reconciled canonical lifecycle with
        // one governing Agreement and TenantAccount. TenantUserAccess is intentionally not required
        // here: an inbound SMS participant may be a valid party without portal credentials. Portal
        // delivery resolves active TenantUserAccess separately.
        return (
            from user in attempt.Persistence.Query<ApplicationUser>()
            join context in attempt.Persistence.Query<WorkspaceAccessContext>()
                on new { UserId = user.Id, PortfolioId = user.PortfolioId!.Value }
                equals new { context.UserId, context.PortfolioId }
            join membership in attempt.Persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in attempt.Persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where user.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= utcNow
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey
                    && profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property)
                && attempt.Persistence.Query<LeaseManagementParty>().Any(party =>
                    party.PortfolioId == portfolioId
                    && party.TenantId == tenantId
                    && attempt.Persistence.Query<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                        lifecycle.PortfolioId == portfolioId
                        && lifecycle.LeaseManagementId == party.LeaseManagementId
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
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PortfolioId == portfolioId
                                    && scope.PropertyId == lifecycle.PropertyId)))))
            select user.Id)
            .Distinct()
            .TagWith("ScopedNotificationRecipients: canonical tenant relationship and assignment scope");
    }
}
