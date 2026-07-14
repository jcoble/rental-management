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
    private static readonly string[] AnyScanCapabilities =
    [
        CapabilityKeys.MoneyExpensesManage,
        CapabilityKeys.MoneyPaymentsManage,
        CapabilityKeys.WorkManage,
        CapabilityKeys.RentalsManage,
        CapabilityKeys.LeasingAgreementsPrepare,
        CapabilityKeys.LeasingApplicationsManage,
    ];

    public static IQueryable<ScanDraft> WhereAuthorizedForReview(
        this IQueryable<ScanDraft> drafts,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime utcNow)
    {
        var expense = WhereForTarget(drafts, db, scope, utcNow, ["Expense"], ExpenseCapabilities);
        var payment = WhereForTarget(drafts, db, scope, utcNow, ["Payment"], PaymentCapabilities);
        var workOrder = WhereForTarget(drafts, db, scope, utcNow, ["WorkOrder"], WorkOrderCapabilities);
        var agreement = WhereForTarget(
            drafts, db, scope, utcNow, [nameof(LeaseAgreement)], AgreementCapabilities);
        var application = WhereForTarget(
            drafts, db, scope, utcNow, ["Application", "RentalApplication"], ApplicationCapabilities);
        var loan = WhereForTarget(drafts, db, scope, utcNow, ["Loan"], LoanCapabilities);
        var pendingClassification = WhereForTarget(
            drafts, db, scope, utcNow, [string.Empty], AnyScanCapabilities);

        // Target types are mutually exclusive, so UNION ALL cannot duplicate a row and remains one
        // translated SQL statement for filtering, counting, sorting, and paging.
        return expense
            .Concat(payment)
            .Concat(workOrder)
            .Concat(agreement)
            .Concat(application)
            .Concat(loan)
            .Concat(pendingClassification);
    }

    public static Task<bool> CanCreateGlobalDraftAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string? targetEntityType,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        // Assigned-work capture is never context-free: a technician must first choose one current
        // responsibility. Managers retain the existing global classification workflow.
        var capabilities = targetEntityType?.Trim() == "WorkOrder"
            ? new[] { CapabilityKeys.WorkManage }
            : CapabilitiesForTarget(targetEntityType);
        return EffectiveAssignments(db, scope, capabilities, utcNow).AnyAsync(ct);
    }

    public static Task<bool> CanCreateDraftAsync(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        string? targetEntityType,
        int? propertyId,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var capabilities = CapabilitiesForTarget(targetEntityType);
        if (capabilities.Count == 0)
            return Task.FromResult(false);

        return propertyId is int selectedPropertyId
            ? db.Properties.AsNoTracking()
                .WhereAuthorized(db, scope, capabilities, utcNow)
                .AnyAsync(property => property.Id == selectedPropertyId, ct)
            : CanCreateGlobalDraftAsync(db, scope, targetEntityType, utcNow, ct);
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
            "" or null => AnyScanCapabilities,
            _ => [],
        };

    private static IQueryable<ScanDraft> WhereForTarget(
        IQueryable<ScanDraft> drafts,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime utcNow,
        IReadOnlyCollection<string> targetTypes,
        IReadOnlyCollection<string> capabilityKeys)
    {
        var capabilities = capabilityKeys.Distinct(StringComparer.Ordinal).ToArray();
        var types = targetTypes.Distinct(StringComparer.Ordinal).ToArray();
        var authorizedProperties = db.Properties.AsNoTracking()
            .WhereAuthorized(db, scope, capabilities, utcNow);
        var authorizedWorkOrders = db.WorkOrders.AsNoTracking()
            .WhereAuthorized(db, scope, capabilities, utcNow);
        var assignments = EffectiveAssignments(db, scope, capabilities, utcNow);
        var allPropertiesAssignments = assignments.Where(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);

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
                 && (draft.CapturePropertyId == null || authorizedProperties.Any(property =>
                        property.Id == draft.CapturePropertyId && property.PortfolioId == draft.PortfolioId) ||
                     draft.CaptureWorkOrderId != null && authorizedWorkOrders.Any(workOrder =>
                        workOrder.Id == draft.CaptureWorkOrderId &&
                        workOrder.PropertyId == draft.CapturePropertyId))
                 && (draft.CaptureUnitId == null || db.Units.AsNoTracking().Any(unit =>
                        unit.Id == draft.CaptureUnitId && unit.PortfolioId == draft.PortfolioId
                        && (authorizedProperties.Any(property =>
                            property.Id == unit.PropertyId && property.PortfolioId == unit.PortfolioId) ||
                            draft.CaptureWorkOrderId != null && authorizedWorkOrders.Any(workOrder =>
                                workOrder.Id == draft.CaptureWorkOrderId && workOrder.UnitId == unit.Id))))
                 && (draft.CaptureLeaseManagementId == null || db.LeaseManagements.AsNoTracking().Any(management =>
                        management.Id == draft.CaptureLeaseManagementId && management.PortfolioId == draft.PortfolioId
                        && authorizedProperties.Any(property =>
                            property.Id == management.PropertyId && property.PortfolioId == management.PortfolioId)))
                 && (draft.CaptureLeaseAgreementId == null || db.LeaseAgreements.AsNoTracking().Any(agreement =>
                        agreement.Id == draft.CaptureLeaseAgreementId && agreement.PortfolioId == draft.PortfolioId
                        && agreement.LeaseManagement != null && authorizedProperties.Any(property =>
                            property.Id == agreement.LeaseManagement.PropertyId
                            && property.PortfolioId == agreement.PortfolioId)))
                 && (draft.CaptureTenantAccountId == null || db.TenantAccounts.AsNoTracking().Any(account =>
                        account.Id == draft.CaptureTenantAccountId && account.PortfolioId == draft.PortfolioId
                        && account.LeaseManagement != null && authorizedProperties.Any(property =>
                            property.Id == account.LeaseManagement.PropertyId
                            && property.PortfolioId == account.PortfolioId)))
                 && (draft.CaptureTenantLedgerEntryId == null || db.TenantLedgerEntries.AsNoTracking().Any(entry =>
                        entry.Id == draft.CaptureTenantLedgerEntryId && entry.PortfolioId == draft.PortfolioId
                        && entry.TenantAccount != null && entry.TenantAccount.LeaseManagement != null
                        && authorizedProperties.Any(property =>
                            property.Id == entry.TenantAccount.LeaseManagement.PropertyId
                            && property.PortfolioId == entry.PortfolioId)))
                 && (draft.CaptureWorkOrderId == null || authorizedWorkOrders.Any(workOrder =>
                        workOrder.Id == draft.CaptureWorkOrderId && workOrder.PortfolioId == draft.PortfolioId
                        && (draft.CapturePropertyId == null || workOrder.PropertyId == draft.CapturePropertyId)))
                 && (draft.CaptureApplicationId == null || db.RentalApplications.AsNoTracking().Any(application =>
                        application.Id == draft.CaptureApplicationId && application.PortfolioId == draft.PortfolioId
                        && application.PropertyId != null && authorizedProperties.Any(property =>
                            property.Id == application.PropertyId && property.PortfolioId == application.PortfolioId)))
                 && (draft.CaptureRentalListingId == null || db.RentalListings.AsNoTracking().Any(listing =>
                        listing.Id == draft.CaptureRentalListingId && listing.PortfolioId == draft.PortfolioId
                        && authorizedProperties.Any(property =>
                            property.Id == listing.PropertyId && property.PortfolioId == listing.PortfolioId))))
                // A global draft is private to its creating access context unless an AllProperties
                // assignment currently carries the target capability.
                || ((draft.CapturePropertyId == null && draft.CaptureUnitId == null
                    && draft.CaptureLeaseManagementId == null && draft.CaptureLeaseAgreementId == null
                    && draft.CaptureTenantAccountId == null && draft.CaptureTenantLedgerEntryId == null
                    && draft.CaptureWorkOrderId == null && draft.CaptureApplicationId == null
                    && draft.CaptureRentalListingId == null)
                    && ((draft.CaptureAccessContextId == scope.AccessContextId && assignments.Any())
                        || allPropertiesAssignments.Any()))
            ));
    }

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        IReadOnlyCollection<string> capabilityKeys,
        DateTime utcNow)
    {
        var keys = capabilityKeys.Distinct(StringComparer.Ordinal).ToArray();
        return db.MembershipRoleAssignments.AsNoTracking().Where(assignment =>
            assignment.PortfolioId == scope.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= utcNow
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
            && assignment.WorkspaceMembership!.AccessContextId == scope.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > utcNow)
            && assignment.WorkspaceMembership.AccessContext!.UserId == scope.UserId
            && assignment.WorkspaceMembership.AccessContext.AccessRevision == scope.AccessRevision
            && assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null
            && db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == scope.SessionId && session.UserId == scope.UserId
                && session.ActiveAccessContextId == scope.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow)
            && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                keys.Contains(profileCapability.CapabilityDefinition!.Key)
                && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
    }
}
