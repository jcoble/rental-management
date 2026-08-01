using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Scanning;

public static class AtomicScanAuthorizationQueries
{
    public static Task<bool> IsAuthorizedForReviewAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceReadScope scope,
        int draftId,
        DateTime utcNow,
        CancellationToken ct = default) =>
        AtomicScanConfirmationPersistence.IsAuthorizedForReviewAsync(db, context, scope, draftId, utcNow, ct);

    public static Task<bool> CanCreateAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime utcNow,
        CancellationToken ct = default) =>
        AtomicScanConfirmationPersistence.CanCreateAuthorizedAsync(
            db, context, scope, targetEntityType, propertyId, utcNow, ct);

    public static Task<bool> IsTenantAccountAuthorizedForPaymentReviewAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceReadScope scope,
        int draftId,
        int tenantAccountId,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var assignments = db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment =>
                assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= utcNow
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow));
        return (
            from draft in db.ScanDrafts.AsNoTracking()
            join account in db.TenantAccounts.AsNoTracking()
                on draft.PortfolioId equals account.PortfolioId
            join management in db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, Id = account.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where draft.Id == draftId
                && draft.PortfolioId == scope.PortfolioId
                && draft.Status == "Reviewing"
                && draft.TargetEntityType == "Payment"
                && account.Id == tenantAccountId
                && account.ClosedAtUtc == null
                && (draft.CaptureLeaseManagementId == null
                    || account.LeaseManagementId == draft.CaptureLeaseManagementId)
                && db.AuthSessions.AsNoTracking().Any(session =>
                    session.Id == scope.SessionId
                    && session.UserId == scope.UserId
                    && session.ActiveAccessContextId == scope.AccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > utcNow)
                && db.WorkspaceAccessContexts.AsNoTracking().Any(context =>
                    context.Id == scope.AccessContextId
                    && context.UserId == scope.UserId
                    && context.PortfolioId == scope.PortfolioId
                    && context.AccessRevision == scope.AccessRevision
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null
                    && context.RevokedAtUtc == null)
                && db.WorkspaceMemberships.AsNoTracking().Any(membership =>
                    membership.AccessContextId == scope.AccessContextId
                    && membership.PortfolioId == scope.PortfolioId
                    && membership.Status == WorkspaceMembershipStatus.Active
                    && membership.SuspendedAtUtc == null
                    && membership.RevokedAtUtc == null
                    && membership.EffectiveFromUtc <= utcNow
                    && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > utcNow)
                    && assignments.Any(assignment =>
                        assignment.WorkspaceMembershipId == membership.Id
                        && assignment.PortfolioId == scope.PortfolioId
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(selected =>
                                selected.PortfolioId == scope.PortfolioId
                                && selected.PropertyId == management.PropertyId))
                        && assignment.RoleProfile!.Capabilities.Any(capability =>
                            capability.CapabilityDefinition!.Key == CapabilityKeys.MoneyPaymentsManage)))
            select account.Id)
            .AnyAsync(ct);
    }
}
