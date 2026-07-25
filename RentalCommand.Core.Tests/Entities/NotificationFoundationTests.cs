using FluentAssertions;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Tests.Entities;

public sealed class NotificationFoundationTests
{
    [Fact]
    public void BroadcastReadState_IsStoredPerPortfolioNotificationAndUser()
    {
        var notificationProperties = typeof(Notification).GetProperties().Select(property => property.Name);
        notificationProperties.Should().NotContain("IsRead");
        notificationProperties.Should().NotContain("ReadAt");

        var readStateProperties = typeof(NotificationReadState).GetProperties()
            .Select(property => property.Name);
        readStateProperties.Should().Contain(nameof(NotificationReadState.PortfolioId));
        readStateProperties.Should().Contain(nameof(NotificationReadState.NotificationId));
        readStateProperties.Should().Contain(nameof(NotificationReadState.UserId));
        readStateProperties.Should().Contain(nameof(NotificationReadState.ReadAt));
    }

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
    public void LegacyMutableBlankNoticeTemplate_IsRemoved()
    {
        typeof(TenantNoticePolicy).Assembly
            .GetType("RentalCommand.Core.Entities.NoticeTemplate")
            .Should().BeNull();
    }

    [Fact]
    public void DeliveryState_DistinguishesProviderAcceptanceFromFinalOutcome()
    {
        Enum.GetNames<NoticeDeliveryState>().Should().Equal(
            "Queued", "Accepted", "Retrying", "Sent", "PermanentlyFailed");
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

    [Fact]
    public void TenantNoticeDraftCapability_IsPropertyScoped_AndAvailableToRentalOperators()
    {
        var capability = AccessCatalog.Capabilities.Single(row =>
            row.Key == CapabilityKeys.TenantNoticesManage);
        var administrator = AccessCatalog.Roles.Single(row =>
            row.Key == RoleProfileKeys.WorkspaceAdministrator);
        var propertyManager = AccessCatalog.Roles.Single(row =>
            row.Key == RoleProfileKeys.PropertyManager);
        var leasingAgent = AccessCatalog.Roles.Single(row =>
            row.Key == RoleProfileKeys.LeasingAgent);

        capability.AuthorizationTargetKind.Should().Be(CapabilityAuthorizationTargetKind.Property);
        AccessCatalog.CapabilityIdsByRole[administrator.Id].Should().Contain(capability.Id);
        AccessCatalog.CapabilityIdsByRole[propertyManager.Id].Should().Contain(capability.Id);
        AccessCatalog.CapabilityIdsByRole[leasingAgent.Id].Should().Contain(capability.Id);
        AccessCatalog.CapabilityIdsByRole
            .Where(pair => pair.Key != administrator.Id &&
                           pair.Key != propertyManager.Id &&
                           pair.Key != leasingAgent.Id)
            .Should().OnlyContain(pair => !pair.Value.Contains(capability.Id));
    }
}
