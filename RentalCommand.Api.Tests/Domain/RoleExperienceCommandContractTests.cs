using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Owners;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Cross-role command contracts. These tests keep each role on its canonical API surface while
/// proving that relationship-scoped Owner lists remain PostgreSQL queries rather than in-memory
/// filtering or paging.
/// </summary>
public sealed class RoleExperienceCommandContractTests
{
    private static readonly OwnerPortalReadScope OwnerScope = new(17, 23, 31, 7);

    [Fact]
    public void LeasingVertical_UsesCanonicalResourceRoutesAndExactScopedCapabilities()
    {
        typeof(ApplicationsController).Should().BeDerivedFrom<ManagementControllerBase>();
        typeof(UnitController).Should().BeDerivedFrom<ManagementControllerBase>();
        typeof(LeaseManagementController).Should().BeDerivedFrom<ManagementControllerBase>();
        typeof(LeaseAgreementController).Should().BeDerivedFrom<ManagementControllerBase>();

        var applications = Source("RentalCommand.Api", "Controllers", "ApplicationsController.cs");
        applications.Should().Contain("_service.ApproveAuthorizedAsync(");
        applications.Should().Contain("_service.DeclineAuthorizedAsync(");
        applications.Should().Contain("_screening.RecordDecisionAsync(");
        applications.Should().Contain("CapabilityKeys.LeasingApplicationsManage");

        var units = Source("RentalCommand.Api", "Controllers", "UnitController.cs");
        units.Should().Contain("WorkspaceExperience.Management or WorkspaceExperience.Leasing");
        units.Should().Contain("CapabilityKeys.LeasingListingsManage");
        units.Should().Contain("_listings.SaveAsync(");

        var moveIn = Source("RentalCommand.Data", "Leasing", "PrepareMoveInRule.cs");
        moveIn.Should().Contain("CapabilityKeys.LeasingAgreementsPrepare");
        moveIn.Should().Contain("candidate.PropertyId == null || candidate.PropertyId == unit.PropertyId");
        moveIn.Should().Contain("candidate.UnitId == null || candidate.UnitId == unit.Id");
        moveIn.Should().Contain("trackedApplication.PropertyId = target.PropertyId");
        moveIn.Should().Contain("trackedApplication.UnitId = target.UnitId");
        var agreements = Source(
            "RentalCommand.Data", "Leasing", "LeaseAgreementDraftRules.cs");
        agreements.Should().Contain("CapabilityKeys.LeasingAgreementsPrepare");
        agreements.Should().Contain("CreateLeaseAgreementSuccessorDraftCommand");

        var workspace = Source(
            "RentalCommand.Api", "Controllers", "LeasingWorkspaceController.cs");
        workspace.Should().Contain("[Route(\"api/v1/leasing\")",
            "the restricted Leasing experience has purpose-built read projections");
        workspace.Should().NotContain("ApproveApplication");
        workspace.Should().NotContain("SaveListing");
        workspace.Should().NotContain("PrepareMoveIn");
        workspace.Should().NotContain("CreateLeaseAgreementSuccessorDraftCommand",
            "mutations stay on their one canonical resource route");
    }

    [Fact]
    public void OwnerSurface_IsRelationshipScopedAndExposesDecisionAndReplyCommands()
    {
        typeof(OwnerPortalController).Should().BeDerivedFrom<AuthenticatedPortfolioControllerBase>();
        typeof(OwnerPortalController).Should().NotBeDerivedFrom<ManagementControllerBase>();

        Route(nameof(OwnerPortalController.DecideApproval), typeof(HttpPostAttribute))
            .Should().Be("approvals/{notificationId:int}/decision");
        Route(nameof(OwnerPortalController.ReplyToMessage), typeof(HttpPostAttribute))
            .Should().Be("messages/{notificationId:int}/replies");

        var handler = Source("RentalCommand.Data", "Owners", "OwnerPortalRules.cs");
        handler.Should().Contain("AuthorizeReplayAsync(");
        handler.Should().Contain("AuthorizeReplayAsync(");
        handler.Should().Contain("db.Set<OwnerUserAccess>().Any(access =>");
        handler.Should().Contain("session.ActiveAccessContextId == actorAccessContextId");
        handler.Should().Contain("CapabilityKeys.MoneyOwnerReportsRead");
        handler.Should().Contain("MembershipRoleAssignmentScopeKind.SelectedProperties");
    }

    [Fact]
    public void OwnerSearchSortAndPaging_TranslateFromEffectiveRelationshipScope()
    {
        using var db = NewContext();
        var service = new OwnerPortalService(db, TimeProvider.System);
        var query = new ListQuery
        {
            Search = "Maple",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Sort = "-createdAt",
            Skip = 20,
            Take = 20,
        };

        var itemSql = service.BuildOwnerItemsQuery(
                OwnerScope, query, OwnerPortalService.ApprovalNotificationType)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToQueryString();
        var distributionSql = service.BuildDistributionsQuery(OwnerScope, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToQueryString();

        foreach (var sql in new[] { itemSql, distributionSql })
        {
            sql.Should().Contain("vw_effective_owner_access");
            sql.Should().Contain("ILIKE");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            sql.Should().Contain("OFFSET");
            sql.Count(character => character == ';').Should().BeLessThanOrEqualTo(1,
                "each owner page must translate to one SQL command");
        }
    }

    [Fact]
    public void StaffNotifications_UseTypedRecordRoutesWithoutQueryStringState()
    {
        var conversation = Source(
            "RentalCommand.Data", "Conversations", "SendConversationMessageRule.cs");
        var vendorDispatch = Source(
            "RentalCommand.Data", "Operations", "CompleteVendorDispatchFromInboundRule.cs");

        conversation.Should().Contain("NavigationDestination = NavigationDestination.Message");
        conversation.Should().Contain("NavigationResourceKind = nameof(Conversation)");
        conversation.Should().NotContain("ActionUrl");
        vendorDispatch.Should().Contain("NavigationDestination.TechnicianWork");
        vendorDispatch.Should().Contain("NavigationDestination.WorkOrder");
        vendorDispatch.Should().NotContain("ActionUrl");
    }

    private static string Route(string actionName, Type attributeType)
    {
        var method = typeof(OwnerPortalController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}.");
        var attribute = method.GetCustomAttributes(attributeType, inherit: true).Single();
        return attribute switch
        {
            HttpPostAttribute post => post.Template!,
            HttpPatchAttribute patch => patch.Template!,
            HttpPutAttribute put => put.Template!,
            HttpGetAttribute get => get.Template!,
            _ => throw new InvalidOperationException($"Unsupported route attribute {attributeType.Name}."),
        };
    }

    private static RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
            .Options);

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. path]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
