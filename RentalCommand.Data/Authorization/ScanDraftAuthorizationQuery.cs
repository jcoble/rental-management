using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Authorization;

/// <summary>
/// Canonical authorization boundary for scan-review records. A draft is either tied to one or
/// more canonical property-backed capture records, or it is global. Property-backed drafts require
/// the target capability on every captured property's current scope. A global draft is visible to
/// its creator, provided that creator still has the target capability, or to an AllProperties
/// assignment carrying that capability.
/// </summary>
public static class ScanDraftAuthorizationQuery
{
    private static readonly string[] ExpenseCapabilities = [CapabilityKeys.MoneyExpensesManage];
    private static readonly string[] PaymentCapabilities = [CapabilityKeys.MoneyPaymentsManage];
    private static readonly string[] WorkOrderCapabilities =
        [CapabilityKeys.WorkManage, CapabilityKeys.AssignedWorkUpdate];
    private static readonly string[] AgreementCapabilities =
        [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingAgreementsPrepare];
    private static readonly string[] ApplicationCapabilities = [CapabilityKeys.LeasingApplicationsManage];
    private static readonly string[] LoanCapabilities = [CapabilityKeys.MoneyExpensesManage];
    private static readonly string[] PropertyAcquisitionCapabilities = [CapabilityKeys.RentalsManage];
    private static readonly string[] LeaseEndingNoticeCapabilities = [CapabilityKeys.RentalsManage];
    private static readonly string[] AnyScanCapabilities =
    [
        CapabilityKeys.MoneyExpensesManage,
        CapabilityKeys.MoneyPaymentsManage,
        CapabilityKeys.WorkManage,
        CapabilityKeys.RentalsManage,
        CapabilityKeys.LeasingAgreementsPrepare,
        CapabilityKeys.LeasingApplicationsManage,
    ];

    public static IQueryable<ScanDraft> WhereAuthorized(
        this IQueryable<ScanDraft> drafts,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime businessUtcNow,
        DateTime? securityUtcNow = null)
    {
        var sessionUtcNow = securityUtcNow ?? TimeProvider.System.GetUtcNow().UtcDateTime;
        var expense = WhereForTarget(drafts, db, scope, businessUtcNow, sessionUtcNow, ["Expense"], ExpenseCapabilities);
        var payment = WhereForTarget(drafts, db, scope, businessUtcNow, sessionUtcNow, ["Payment"], PaymentCapabilities);
        var workOrder = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow, ["WorkOrder"], WorkOrderCapabilities);
        var agreement = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow, [nameof(LeaseAgreement)], AgreementCapabilities);
        var application = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow,
            ["Application", "RentalApplication"], ApplicationCapabilities);
        var loan = WhereForTarget(drafts, db, scope, businessUtcNow, sessionUtcNow, ["Loan"], LoanCapabilities);
        var propertyAcquisition = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow,
            ["PropertyAcquisition"], PropertyAcquisitionCapabilities);
        var leaseEndingNotice = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow,
            ["LeaseEndingNotice"], LeaseEndingNoticeCapabilities);
        var pendingClassification = WhereForTarget(
            drafts, db, scope, businessUtcNow, sessionUtcNow, [string.Empty], AnyScanCapabilities);

        // Target types are mutually exclusive, so UNION ALL cannot duplicate a row and remains one
        // translated SQL statement for filtering, counting, sorting, and paging.
        return expense
            .Concat(payment)
            .Concat(workOrder)
            .Concat(agreement)
            .Concat(application)
            .Concat(loan)
            .Concat(propertyAcquisition)
            .Concat(leaseEndingNotice)
            .Concat(pendingClassification);
    }

    public static Task<bool> CanCreateGlobalDraftAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string? targetEntityType,
        DateTime businessUtcNow,
        DateTime? securityUtcNow = null,
        CancellationToken ct = default)
    {
        var sessionUtcNow = securityUtcNow ?? TimeProvider.System.GetUtcNow().UtcDateTime;
        // Assigned-work capture is never context-free: a technician must first choose one current
        // responsibility. Managers retain the existing global classification workflow.
        var capabilities = targetEntityType?.Trim() == "WorkOrder"
            ? new[] { CapabilityKeys.WorkManage }
            : CapabilitiesForTarget(targetEntityType);
        return EffectiveAssignments(db, scope, capabilities, businessUtcNow, sessionUtcNow).AnyAsync(ct);
    }

    public static Task<bool> CanCreateDraftAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime businessUtcNow,
        DateTime? securityUtcNow = null,
        CancellationToken ct = default)
    {
        var capabilities = CapabilitiesForTarget(targetEntityType);
        if (capabilities.Count == 0)
            return Task.FromResult(false);

        return propertyId is int selectedPropertyId
            ? db.Properties.AsNoTracking()
                .WhereAuthorized(db, scope, capabilities, businessUtcNow)
                .AnyAsync(property => property.Id == selectedPropertyId, ct)
            : CanCreateGlobalDraftAsync(db, scope, targetEntityType, businessUtcNow, securityUtcNow, ct);
    }

    public static IReadOnlyCollection<string> CapabilitiesForTarget(string? targetEntityType) =>
        targetEntityType?.Trim() switch
        {
            "Expense" => ExpenseCapabilities,
            "Payment" => PaymentCapabilities,
            "WorkOrder" => WorkOrderCapabilities,
            nameof(LeaseAgreement) => AgreementCapabilities,
            "Application" or "RentalApplication" => ApplicationCapabilities,
            "Loan" => LoanCapabilities,
            "PropertyAcquisition" => PropertyAcquisitionCapabilities,
            "LeaseEndingNotice" => LeaseEndingNoticeCapabilities,
            "" or null => AnyScanCapabilities,
            _ => [],
        };

    private static IQueryable<ScanDraft> WhereForTarget(
        IQueryable<ScanDraft> drafts,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime businessUtcNow,
        DateTime securityUtcNow,
        IReadOnlyCollection<string> targetTypes,
        IReadOnlyCollection<string> capabilityKeys)
    {
        var capabilities = capabilityKeys.Distinct(StringComparer.Ordinal).ToArray();
        var types = targetTypes.Distinct(StringComparer.Ordinal).ToArray();
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilities, businessUtcNow);
        var authorizedWorkOrders = db.WorkOrders.AsNoTracking()
            .WhereAuthorized(db, scope, capabilities, businessUtcNow);
        var assignments = EffectiveAssignments(
            db,
            scope,
            capabilities,
            businessUtcNow,
            securityUtcNow);
        var supportsAssignedWork = types.Contains("WorkOrder", StringComparer.Ordinal)
            && capabilities.Contains(CapabilityKeys.AssignedWorkUpdate, StringComparer.Ordinal);

        return drafts.Where(draft =>
            draft.PortfolioId == scope.PortfolioId &&
            types.Contains(draft.TargetEntityType) &&
            (
                // A contextual draft must remain authorized through every captured relationship.
                ((draft.CapturePropertyId != null || draft.CaptureUnitId != null
                    || draft.CaptureLeaseManagementId != null || draft.CaptureLeaseAgreementId != null
                    || draft.CaptureTenantAccountId != null || draft.CaptureTenantLedgerEntryId != null
                    || draft.CaptureWorkOrderId != null || draft.CaptureApplicationId != null
                    || draft.CaptureRentalListingId != null)
                 && (
                     // A capture context represents one property-backed workflow. Resolve all
                     // captured records against that same authorized property so the capability
                     // scope is evaluated once instead of once per nullable relationship.
                     authorizedProperties.Any(property =>
                         property.PortfolioId == draft.PortfolioId
                         && (draft.CapturePropertyId == null || property.Id == draft.CapturePropertyId)
                         && (draft.CaptureUnitId == null || db.Units.AsNoTracking().Any(unit =>
                             unit.Id == draft.CaptureUnitId
                             && unit.PortfolioId == draft.PortfolioId
                             && unit.PropertyId == property.Id))
                         && (draft.CaptureLeaseManagementId == null || db.LeaseManagements.AsNoTracking().Any(management =>
                             management.Id == draft.CaptureLeaseManagementId
                             && management.PortfolioId == draft.PortfolioId
                             && management.PropertyId == property.Id))
                         && (draft.CaptureLeaseAgreementId == null || db.LeaseAgreements.AsNoTracking().Any(agreement =>
                             agreement.Id == draft.CaptureLeaseAgreementId
                             && agreement.PortfolioId == draft.PortfolioId
                             && agreement.LeaseManagement != null
                             && agreement.LeaseManagement.PropertyId == property.Id))
                         && (draft.CaptureTenantAccountId == null || db.TenantAccounts.AsNoTracking().Any(account =>
                             account.Id == draft.CaptureTenantAccountId
                             && account.PortfolioId == draft.PortfolioId
                             && account.LeaseManagement != null
                             && account.LeaseManagement.PropertyId == property.Id))
                         && (draft.CaptureTenantLedgerEntryId == null || db.TenantLedgerEntries.AsNoTracking().Any(entry =>
                             entry.Id == draft.CaptureTenantLedgerEntryId
                             && entry.PortfolioId == draft.PortfolioId
                             && entry.TenantAccount != null
                             && entry.TenantAccount.LeaseManagement != null
                             && entry.TenantAccount.LeaseManagement.PropertyId == property.Id))
                         && (draft.CaptureWorkOrderId == null || db.WorkOrders.AsNoTracking().Any(workOrder =>
                             workOrder.Id == draft.CaptureWorkOrderId
                             && workOrder.PortfolioId == draft.PortfolioId
                             && workOrder.PropertyId == property.Id))
                         && (draft.CaptureApplicationId == null || db.RentalApplications.AsNoTracking().Any(application =>
                             application.Id == draft.CaptureApplicationId
                             && application.PortfolioId == draft.PortfolioId
                             && application.PropertyId == property.Id))
                         && (draft.CaptureRentalListingId == null || db.RentalListings.AsNoTracking().Any(listing =>
                             listing.Id == draft.CaptureRentalListingId
                             && listing.PortfolioId == draft.PortfolioId
                             && listing.PropertyId == property.Id)))
                     // Assigned-work access is intentionally narrower than property access.
                     // It can authorize only a context tied to the exact current work order.
                     || (supportsAssignedWork
                         && draft.CaptureWorkOrderId != null
                         && authorizedWorkOrders.Any(workOrder =>
                         workOrder.Id == draft.CaptureWorkOrderId
                         && workOrder.PortfolioId == draft.PortfolioId
                         && (draft.CapturePropertyId == null || workOrder.PropertyId == draft.CapturePropertyId)
                         && (draft.CaptureUnitId == null || workOrder.UnitId == draft.CaptureUnitId)
                         && (draft.CaptureLeaseManagementId == null || db.LeaseManagements.AsNoTracking().Any(management =>
                             management.Id == draft.CaptureLeaseManagementId
                             && management.PortfolioId == draft.PortfolioId
                             && management.PropertyId == workOrder.PropertyId))
                         && (draft.CaptureLeaseAgreementId == null || db.LeaseAgreements.AsNoTracking().Any(agreement =>
                             agreement.Id == draft.CaptureLeaseAgreementId
                             && agreement.PortfolioId == draft.PortfolioId
                             && agreement.LeaseManagement != null
                             && agreement.LeaseManagement.PropertyId == workOrder.PropertyId))
                         && (draft.CaptureTenantAccountId == null || db.TenantAccounts.AsNoTracking().Any(account =>
                             account.Id == draft.CaptureTenantAccountId
                             && account.PortfolioId == draft.PortfolioId
                             && account.LeaseManagement != null
                             && account.LeaseManagement.PropertyId == workOrder.PropertyId))
                         && (draft.CaptureTenantLedgerEntryId == null || db.TenantLedgerEntries.AsNoTracking().Any(entry =>
                             entry.Id == draft.CaptureTenantLedgerEntryId
                             && entry.PortfolioId == draft.PortfolioId
                             && entry.TenantAccount != null
                             && entry.TenantAccount.LeaseManagement != null
                             && entry.TenantAccount.LeaseManagement.PropertyId == workOrder.PropertyId))
                         && (draft.CaptureApplicationId == null || db.RentalApplications.AsNoTracking().Any(application =>
                             application.Id == draft.CaptureApplicationId
                             && application.PortfolioId == draft.PortfolioId
                             && application.PropertyId == workOrder.PropertyId))
                         && (draft.CaptureRentalListingId == null || db.RentalListings.AsNoTracking().Any(listing =>
                             listing.Id == draft.CaptureRentalListingId
                             && listing.PortfolioId == draft.PortfolioId
                             && listing.PropertyId == workOrder.PropertyId))))
                 ))
                // A global draft is private to its creating access context unless an AllProperties
                // assignment currently carries the target capability.
                || ((draft.CapturePropertyId == null && draft.CaptureUnitId == null
                    && draft.CaptureLeaseManagementId == null && draft.CaptureLeaseAgreementId == null
                    && draft.CaptureTenantAccountId == null && draft.CaptureTenantLedgerEntryId == null
                    && draft.CaptureWorkOrderId == null && draft.CaptureApplicationId == null
                    && draft.CaptureRentalListingId == null)
                    && assignments.Any(assignment =>
                        draft.CaptureAccessContextId == scope.AccessContextId
                        || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties))
            ));
    }

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime businessUtcNow,
        DateTime securityUtcNow)
    {
        var keys = capabilityKeys.Distinct(StringComparer.Ordinal).ToArray();
        return db.MembershipRoleAssignments.AsNoTracking().Where(assignment =>
            assignment.PortfolioId == scope.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= businessUtcNow
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessUtcNow)
            && assignment.WorkspaceMembership!.AccessContextId == scope.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= businessUtcNow
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > businessUtcNow)
            && assignment.WorkspaceMembership.AccessContext!.UserId == scope.UserId
            && assignment.WorkspaceMembership.AccessContext.AccessRevision == scope.AccessRevision
            && assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null
            && db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == scope.SessionId && session.UserId == scope.UserId
                && session.ActiveAccessContextId == scope.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityUtcNow)
            && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                keys.Contains(profileCapability.CapabilityDefinition!.Key)
                && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
    }
}
