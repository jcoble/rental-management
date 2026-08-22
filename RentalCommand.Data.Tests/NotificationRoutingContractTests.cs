using FluentAssertions;

namespace RentalCommand.Data.Tests;

public sealed class NotificationRoutingContractTests
{
    [Fact]
    public void TeamRouting_UsesCurrentCapabilityScopeResponsibilityAndVisibleFallbackBranches()
    {
        var source = Read("RentalCommand.Data", "Notifications", "ScopedNotificationRecipientQuery.cs");

        source.Should().Contain("ForTeamTopic");
        source.Should().Contain("ForTenantTeamTopic");
        source.Should().Contain("TeamRoutingRuleRecipient");
        source.Should().Contain("WorkOrderResponsibility");
        source.Should().Contain("CapabilityKeys.AssignedWorkRead");
        source.Should().Contain("context.AccessRevision > 0");
        source.Should().Contain("RoleProfileKeys.WorkspaceAdministrator");
        source.Should().NotContain("public static IQueryable<int> ForProperty(");
        source.Should().NotContain("public static IQueryable<int> ForTenantRelationship(");
    }

    [Fact]
    public void ExistingTenantAndWorkOrderEvents_UseCanonicalTeamRouting()
    {
        Read("RentalCommand.Data", "Conversations", "SendConversationMessageRule.cs")
            .Should().Contain(".ForTenantTeamTopic(")
            .And.NotContain(".ForTenantRelationship(");
        Read("RentalCommand.Data", "Operations", "CompleteVendorDispatchFromInboundRule.cs")
            .Should().Contain(".ForTeamTopic(")
            .And.NotContain(".ForProperty(");
    }

    private static string Read(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run below the repository root");
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray()));
    }
}
