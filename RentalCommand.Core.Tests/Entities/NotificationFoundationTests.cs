using FluentAssertions;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Tests.Entities;

public sealed class NotificationFoundationTests
{
    [Fact]
    public void TenantNoticePolicy_DefaultsToDraft_AndHasNoMasterTenantSwitch()
    {
        var policy = new TenantNoticePolicy();

        policy.Mode.Should().Be(TenantNoticeMode.Draft);
        var propertyNames = typeof(TenantNoticePolicy).GetProperties().Select(property => property.Name);
        propertyNames.Should().NotContain("NotifyTenants");
        propertyNames.Should().NotContain("SendTenantNotices");
    }

    [Fact]
    public void LegalAuto_RequiresReviewedJurisdictionFacts()
    {
        var policy = new TenantNoticePolicy
        {
            Mode = TenantNoticeMode.Auto,
            Classification = NoticeClassification.Legal,
        };

        policy.CanAutoSend.Should().BeFalse();
        policy.ReviewedJurisdictionCode = "US-PA";
        policy.JurisdictionReviewedAtUtc = DateTime.UtcNow;
        policy.CanAutoSend.Should().BeTrue();
    }

    [Fact]
    public void NotificationAdministrationCapability_IsWorkspaceScoped_AndAdministratorOwned()
    {
        var capability = AccessCatalog.Capabilities.Single(row => row.Key == CapabilityKeys.NotificationsManage);
        var administrator = AccessCatalog.Roles.Single(row => row.Key == RoleProfileKeys.WorkspaceAdministrator);

        capability.AuthorizationTargetKind.Should().Be(CapabilityAuthorizationTargetKind.Workspace);
        AccessCatalog.CapabilityIdsByRole[administrator.Id].Should().Contain(capability.Id);
        AccessCatalog.CapabilityIdsByRole
            .Where(pair => pair.Key != administrator.Id)
            .Should().OnlyContain(pair => !pair.Value.Contains(capability.Id));
    }
}
