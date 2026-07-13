using FluentAssertions;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Tests.Auth;

public sealed class RolePresetCapabilityTests
{
    [Fact]
    public void FourCanonicalPresets_HaveReviewedExperienceScopeAndCapabilityBoundaries()
    {
        AccessCatalog.Roles.Select(role => role.Key).Should().Equal(
            RoleProfileKeys.WorkspaceAdministrator,
            RoleProfileKeys.PropertyManager,
            RoleProfileKeys.LeasingAgent,
            RoleProfileKeys.MaintenanceTechnician);

        var administrator = Role(RoleProfileKeys.WorkspaceAdministrator);
        administrator.DefaultExperience.Should().Be(WorkspaceExperience.Management);
        administrator.DefaultScopeKind.Should().Be(MembershipRoleAssignmentScopeKind.AllProperties);
        CapabilityKeysFor(administrator).Should().BeEquivalentTo(
            AccessCatalog.Capabilities.Select(capability => capability.Key));

        var propertyManager = Role(RoleProfileKeys.PropertyManager);
        propertyManager.DefaultExperience.Should().Be(WorkspaceExperience.Management);
        propertyManager.DefaultScopeKind.Should().Be(MembershipRoleAssignmentScopeKind.SelectedProperties);
        CapabilityKeysFor(propertyManager).Should().BeEquivalentTo(
            [
                CapabilityKeys.RentalsRead,
                CapabilityKeys.RentalsManage,
                CapabilityKeys.WorkRead,
                CapabilityKeys.WorkManage,
                CapabilityKeys.ReportsRead,
                CapabilityKeys.MoneyBalancesRead,
                CapabilityKeys.MoneyChargesManage,
                CapabilityKeys.MoneyPaymentsManage,
                CapabilityKeys.MoneyExpensesManage,
                CapabilityKeys.MoneyDepositsManage,
                CapabilityKeys.MoneyOwnerReportsRead,
                CapabilityKeys.MoneyReconciliationOperate,
                CapabilityKeys.ResponsibilityAssignExistingMember,
                CapabilityKeys.TenantNoticesManage,
            ]);

        var leasingAgent = Role(RoleProfileKeys.LeasingAgent);
        leasingAgent.DefaultExperience.Should().Be(WorkspaceExperience.Leasing);
        leasingAgent.DefaultScopeKind.Should().Be(MembershipRoleAssignmentScopeKind.SelectedProperties);
        CapabilityKeysFor(leasingAgent).Should().BeEquivalentTo(
            [
                CapabilityKeys.RentalsRead,
                CapabilityKeys.LeasingListingsManage,
                CapabilityKeys.LeasingApplicationsManage,
                CapabilityKeys.LeasingApplicationFeesCollect,
                CapabilityKeys.LeasingShowingsManage,
                CapabilityKeys.LeasingAgreementsPrepare,
                CapabilityKeys.LeasingOnboardingManage,
                CapabilityKeys.LeasingTermsRead,
                CapabilityKeys.LeasingDepositsRead,
            ]);

        var technician = Role(RoleProfileKeys.MaintenanceTechnician);
        technician.DefaultExperience.Should().Be(WorkspaceExperience.Maintenance);
        technician.DefaultScopeKind.Should().Be(MembershipRoleAssignmentScopeKind.AssignedWorkOrders);
        CapabilityKeysFor(technician).Should().BeEquivalentTo(
            [
                CapabilityKeys.AssignedWorkRead,
                CapabilityKeys.AssignedWorkUpdate,
                CapabilityKeys.AssignedWorkConverse,
                CapabilityKeys.AssignedWorkTimeMaterialsManage,
            ]);
    }

    [Fact]
    public void PropertyManager_CannotAdministerWorkspaceOrMoveMoneyOutsideOperations()
    {
        var propertyManagerCapabilities = CapabilityKeysFor(Role(RoleProfileKeys.PropertyManager));
        string[] forbiddenCapabilities =
        [
            CapabilityKeys.TeamRead,
            CapabilityKeys.TeamManage,
            CapabilityKeys.SecurityManage,
            CapabilityKeys.BillingManage,
            CapabilityKeys.IntegrationsManage,
            CapabilityKeys.DataExport,
            CapabilityKeys.BankConnectionsManage,
            CapabilityKeys.PayoutsManage,
            CapabilityKeys.MoneyDisbursementsManage,
            CapabilityKeys.MoneyReconciliationDestructive,
            CapabilityKeys.AccountDestructiveActions,
            CapabilityKeys.NotificationsManage,
        ];

        foreach (var capability in forbiddenCapabilities)
        {
            propertyManagerCapabilities.Should().NotContain(capability);
        }
    }

    private static AccessCatalog.RoleSeed Role(string key) =>
        AccessCatalog.Roles.Single(role => role.Key == key);

    private static string[] CapabilityKeysFor(AccessCatalog.RoleSeed role)
    {
        var capabilityIds = AccessCatalog.CapabilityIdsByRole[role.Id];
        return AccessCatalog.Capabilities
            .Where(capability => capabilityIds.Contains(capability.Id))
            .Select(capability => capability.Key)
            .ToArray();
    }
}
