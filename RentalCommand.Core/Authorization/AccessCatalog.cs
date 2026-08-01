using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Authorization;

public static class RoleProfileKeys
{
    public const string WorkspaceAdministrator = "workspace-administrator";
    public const string PropertyManager = "property-manager";
    public const string LeasingAgent = "leasing-agent";
    public const string MaintenanceTechnician = "maintenance-technician";
    public const string OwnerPortal = "owner-portal";
    public const string TenantPortal = "tenant-portal";
}

public static class CapabilityKeys
{
    public const string RentalsRead = "rentals.read";
    public const string RentalsManage = "rentals.manage";
    public const string WorkRead = "work.read";
    public const string WorkManage = "work.manage";
    public const string ReportsRead = "reports.read";
    public const string MoneyBalancesRead = "money.balances.read";
    public const string MoneyChargesManage = "money.charges.manage";
    public const string MoneyPaymentsManage = "money.payments.manage";
    public const string MoneyExpensesManage = "money.expenses.manage";
    public const string MoneyDepositsManage = "money.deposits.manage";
    public const string MoneyOwnerReportsRead = "money.owner-reports.read";
    public const string MoneyReconciliationOperate = "money.reconciliation.operate";
    public const string ResponsibilityAssignExistingMember = "responsibility.assign-existing-member";
    public const string LeasingListingsManage = "leasing.listings.manage";
    public const string LeasingApplicationsManage = "leasing.applications.manage";
    public const string LeasingApplicationFeesCollect = "leasing.application-fees.collect";
    public const string LeasingShowingsManage = "leasing.showings.manage";
    public const string LeasingAgreementsPrepare = "leasing.agreements.prepare";
    public const string LeasingOnboardingManage = "leasing.onboarding.manage";
    public const string LeasingTermsRead = "leasing.terms.read";
    public const string LeasingDepositsRead = "leasing.deposits.read";
    public const string AssignedWorkRead = "maintenance.assigned-work.read";
    public const string AssignedWorkUpdate = "maintenance.assigned-work.update";
    public const string AssignedWorkConverse = "maintenance.assigned-work.converse";
    public const string AssignedWorkTimeMaterialsManage = "maintenance.assigned-work.time-materials.manage";
    public const string TeamRead = "team.read";
    public const string TeamManage = "team.manage";
    public const string SecurityManage = "security.manage";
    public const string BillingManage = "billing.manage";
    public const string IntegrationsManage = "integrations.manage";
    public const string DataExport = "data.export";
    public const string BankConnectionsManage = "bank-connections.manage";
    public const string PayoutsManage = "payouts.manage";
    public const string MoneyDisbursementsManage = "money.disbursements.manage";
    public const string MoneyReconciliationDestructive = "money.reconciliation.destructive";
    public const string AccountDestructiveActions = "account.destructive-actions";
    public const string NotificationsManage = "notifications.manage";
    public const string TenantNoticesManage = "notifications.tenant-notices.manage";
}

/// <summary>
/// Stable seed IDs and the reviewed capability split. IDs are internal persistence keys; consumers
/// authorize with immutable string keys.
/// </summary>
public static class AccessCatalog
{
    public sealed record RoleSeed(
        int Id,
        string Key,
        string DisplayName,
        string Description,
        WorkspaceExperience DefaultExperience,
        MembershipRoleAssignmentScopeKind DefaultScopeKind);

    public sealed record CapabilitySeed(
        int Id,
        string Key,
        string Description,
        CapabilityAuthorizationTargetKind AuthorizationTargetKind);

    public static readonly IReadOnlyList<RoleSeed> Roles =
    [
        new(1, RoleProfileKeys.WorkspaceAdministrator, "Workspace Administrator",
            "Full workspace authority, including Team, security, billing, integrations, banking, payouts, exports, and destructive actions.",
            WorkspaceExperience.Management, MembershipRoleAssignmentScopeKind.AllProperties),
        new(2, RoleProfileKeys.PropertyManager, "Property Manager",
            "Daily rental, work, and operational money authority within independently assigned property scope.",
            WorkspaceExperience.Management, MembershipRoleAssignmentScopeKind.SelectedProperties),
        new(3, RoleProfileKeys.LeasingAgent, "Leasing Agent",
            "Listings, applications, showings, agreement preparation, and onboarding within assigned property scope.",
            WorkspaceExperience.Leasing, MembershipRoleAssignmentScopeKind.SelectedProperties),
        new(4, RoleProfileKeys.MaintenanceTechnician, "Maintenance Technician",
            "Assigned-work-only maintenance access with no general property or Unit browsing.",
            WorkspaceExperience.Maintenance, MembershipRoleAssignmentScopeKind.AssignedWorkOrders),
        new(5, RoleProfileKeys.OwnerPortal, "Owner Portal",
            "Owner relationship access only. Grants no management, leasing, maintenance, or workspace capabilities.",
            WorkspaceExperience.Owner, MembershipRoleAssignmentScopeKind.AllProperties),
        new(6, RoleProfileKeys.TenantPortal, "Tenant Portal",
            "Tenant relationship access only. Grants no management, leasing, maintenance, or workspace capabilities.",
            WorkspaceExperience.Tenant, MembershipRoleAssignmentScopeKind.AllProperties),
    ];

    public static readonly IReadOnlyList<CapabilitySeed> Capabilities =
    [
        new(1, CapabilityKeys.RentalsRead, "Read in-scope management rental records.", CapabilityAuthorizationTargetKind.Property),
        new(2, CapabilityKeys.RentalsManage, "Manage in-scope rental operations.", CapabilityAuthorizationTargetKind.Property),
        new(3, CapabilityKeys.WorkRead, "Read in-scope work operations.", CapabilityAuthorizationTargetKind.Property),
        new(4, CapabilityKeys.WorkManage, "Manage in-scope work operations.", CapabilityAuthorizationTargetKind.Property),
        new(5, CapabilityKeys.ReportsRead, "Read in-scope operational reports.", CapabilityAuthorizationTargetKind.Property),
        new(6, CapabilityKeys.MoneyBalancesRead, "Read in-scope balances.", CapabilityAuthorizationTargetKind.Property),
        new(7, CapabilityKeys.MoneyChargesManage, "Manage in-scope charges.", CapabilityAuthorizationTargetKind.Property),
        new(8, CapabilityKeys.MoneyPaymentsManage, "Manage in-scope payments.", CapabilityAuthorizationTargetKind.Property),
        new(9, CapabilityKeys.MoneyExpensesManage, "Manage in-scope expenses.", CapabilityAuthorizationTargetKind.Property),
        new(10, CapabilityKeys.MoneyDepositsManage, "Manage in-scope deposits.", CapabilityAuthorizationTargetKind.Property),
        new(11, CapabilityKeys.MoneyOwnerReportsRead, "Read in-scope owner reporting.", CapabilityAuthorizationTargetKind.Property),
        new(12, CapabilityKeys.MoneyReconciliationOperate, "Perform non-destructive operational reconciliation in scope.", CapabilityAuthorizationTargetKind.Property),
        new(13, CapabilityKeys.ResponsibilityAssignExistingMember, "Assign work or leasing responsibility to an existing in-scope member.", CapabilityAuthorizationTargetKind.Property),
        new(14, CapabilityKeys.LeasingListingsManage, "Manage in-scope listings.", CapabilityAuthorizationTargetKind.Property),
        new(15, CapabilityKeys.LeasingApplicationsManage, "Manage in-scope applications.", CapabilityAuthorizationTargetKind.Property),
        new(16, CapabilityKeys.LeasingShowingsManage, "Manage in-scope showings.", CapabilityAuthorizationTargetKind.Property),
        new(17, CapabilityKeys.LeasingAgreementsPrepare, "Prepare in-scope agreements without financial administration authority.", CapabilityAuthorizationTargetKind.Property),
        new(18, CapabilityKeys.LeasingOnboardingManage, "Manage in-scope leasing onboarding.", CapabilityAuthorizationTargetKind.Property),
        new(19, CapabilityKeys.LeasingTermsRead, "Read terms required for leasing work.", CapabilityAuthorizationTargetKind.Property),
        new(20, CapabilityKeys.LeasingDepositsRead, "Read deposit facts required for leasing work.", CapabilityAuthorizationTargetKind.Property),
        new(21, CapabilityKeys.AssignedWorkRead, "Read only work orders assigned to the member.", CapabilityAuthorizationTargetKind.WorkOrder),
        new(22, CapabilityKeys.AssignedWorkUpdate, "Update only work orders assigned to the member.", CapabilityAuthorizationTargetKind.WorkOrder),
        new(23, CapabilityKeys.AssignedWorkConverse, "Use conversations attached to assigned work.", CapabilityAuthorizationTargetKind.WorkOrder),
        new(24, CapabilityKeys.AssignedWorkTimeMaterialsManage, "Record time and materials on assigned work.", CapabilityAuthorizationTargetKind.WorkOrder),
        new(25, CapabilityKeys.TeamRead, "Read Team configuration.", CapabilityAuthorizationTargetKind.Workspace),
        new(26, CapabilityKeys.TeamManage, "Invite members and manage Team roles and scope.", CapabilityAuthorizationTargetKind.Workspace),
        new(27, CapabilityKeys.SecurityManage, "Manage workspace security settings.", CapabilityAuthorizationTargetKind.Workspace),
        new(28, CapabilityKeys.BillingManage, "Manage subscription and billing settings.", CapabilityAuthorizationTargetKind.Workspace),
        new(29, CapabilityKeys.IntegrationsManage, "Connect and administer external integrations and credentials.", CapabilityAuthorizationTargetKind.Workspace),
        new(30, CapabilityKeys.DataExport, "Export workspace data.", CapabilityAuthorizationTargetKind.Workspace),
        new(31, CapabilityKeys.BankConnectionsManage, "Connect, remove, or administer bank connections.", CapabilityAuthorizationTargetKind.Workspace),
        new(32, CapabilityKeys.PayoutsManage, "Manage payout destinations and payouts.", CapabilityAuthorizationTargetKind.Workspace),
        new(33, CapabilityKeys.MoneyDisbursementsManage, "Initiate transfers and owner distributions.", CapabilityAuthorizationTargetKind.Workspace),
        new(34, CapabilityKeys.MoneyReconciliationDestructive, "Perform destructive reconciliation actions.", CapabilityAuthorizationTargetKind.Workspace),
        new(35, CapabilityKeys.AccountDestructiveActions, "Perform destructive workspace or account actions.", CapabilityAuthorizationTargetKind.Workspace),
        new(36, CapabilityKeys.LeasingApplicationFeesCollect, "Collect in-scope application fees without refund authority.", CapabilityAuthorizationTargetKind.Property),
        new(37, CapabilityKeys.NotificationsManage, "Manage Team routing, tenant notice policy, delivery configuration, and notice templates.", CapabilityAuthorizationTargetKind.Workspace),
        new(38, CapabilityKeys.TenantNoticesManage, "Manage tenant-notice drafts within assigned property scope.", CapabilityAuthorizationTargetKind.Property),
    ];

    public static readonly IReadOnlyDictionary<int, IReadOnlyList<int>> CapabilityIdsByRole =
        new Dictionary<int, IReadOnlyList<int>>
        {
            [1] = Capabilities.Select(capability => capability.Id).ToArray(),
            [2] = Enumerable.Range(1, 13).Append(38).ToArray(),
            [3] = Enumerable.Range(14, 7).Prepend(1).Append(36).Append(38).ToArray(),
            [4] = Enumerable.Range(21, 4).ToArray(),
            [5] = [],
            [6] = [],
        };
}
