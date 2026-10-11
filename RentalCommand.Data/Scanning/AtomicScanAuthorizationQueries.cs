using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;

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
        var assignments = db.AuthorizedAssignmentsForScope(
            scope,
            [CapabilityKeys.MoneyPaymentsManage],
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
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
                && assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == scope.PortfolioId
                        && selected.PropertyId == management.PropertyId))
            select account.Id)
            .AnyAsync(ct);
    }
}
