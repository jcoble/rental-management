using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Auth;

public sealed class LeasingExperienceContractTests
{
    [Fact]
    public void LeasingWorkspaceExposesOnlyPurposeBuiltRoutes()
    {
        var routes = typeof(LeasingWorkspaceController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .Cast<HttpGetAttribute>())
            .Select(attribute => attribute.Template)
            .ToArray();

        routes.Should().BeEquivalentTo(
            "today", "pipeline/page", "rentals/page", "calendar/page", "inbox/page",
            "rentals/{unitId:int}", "applications/{id:int}", "appointments/{id:int}",
            "conversations/{id:int}", "move-ins/{id:int}");
        typeof(LeasingWorkspaceController).BaseType.Should().Be(typeof(ManagementControllerBase));
    }

    [Fact]
    public void LeasingProjectionsRemainDatabaseSideAndCapabilityScoped()
    {
        var source = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeasingWorkspaceService.cs");
        var leaseManagementQuerySource = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeaseManagementQueryService.cs");

        source.Should().Contain(nameof(CapabilityKeys.LeasingApplicationsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingListingsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingShowingsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingOnboardingManage));
        source.Should().Contain("WhereAuthorized");
        source.Should().Contain("CountAsync");
        source.Should().Contain("Skip(query.NormalizedSkip).Take(query.NormalizedTake)");
        source.Should().Contain("applicationProperties.Any(property => property.Id == application.PropertyId)");
        source.Should().Contain("showingProperties.Any(property => property.Id == appointment.PropertyId)");
        source.Should().Contain("scope, CapabilityKeys.LeasingApplicationsManage, now");
        source.Should().Contain("scope, CapabilityKeys.LeasingShowingsManage, now");
        source.Should().Contain("CanViewApplications = applicationProperties.Any(property => property.Id == unit.PropertyId)");
        source.Should().Contain("CanViewShowings = showingProperties.Any(property => property.Id == unit.PropertyId)");
        source.Should().Contain("_db, scope, [CapabilityKeys.LeasingOnboardingManage], now");
        source.Should().NotContain("[CapabilityKeys.LeasingOnboardingManage, CapabilityKeys.LeasingApplicationsManage]");
        source.Should().Contain("_db.LeaseManagementLifecycleProjections.Any(lifecycle =>");
        source.Should().Contain(
            "agreement.Id == (lifecycle.CurrentAgreementId ?? lifecycle.UpcomingAgreementId)");
        source.Should().Contain("agreement.FullyExecutedAtUtc != null");
        source.Should().Contain("agreement.VoidedAtUtc == null");
        leaseManagementQuerySource.Should().Contain("BuildLeaseWorkspaceReadManagementQuery");
        leaseManagementQuerySource.Should().Contain("[CapabilityKeys.RentalsRead, CapabilityKeys.LeasingAgreementsPrepare]");
        source.Should().NotContain("AgreementFullyExecuted = management.Agreements.Any");
        source.Should().NotContain("AsEnumerable");
        source.Should().NotContain("GroupBy(");
    }

    [Fact]
    public void LeasingTodayUsesCanonicalScopeAndCapabilityScopedCounts()
    {
        var source = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeasingWorkspaceService.cs");

        source.Should().Contain("public Task<LeasingTodayResponse?> GetTodayAsync");
        source.Should().Contain("_db.Portfolios");
        source.Should().Contain("portfolio.Id == scope.PortfolioId");
        source.Should().NotContain("_db.AuthSessions");
        source.Should().Contain("scope, CapabilityKeys.LeasingApplicationsManage, now");
        source.Should().Contain("scope, CapabilityKeys.LeasingListingsManage, now");
        source.Should().Contain("scope, CapabilityKeys.LeasingShowingsManage, now");
        source.Should().Contain("[CapabilityKeys.LeasingOnboardingManage], now");
        source.Should().Contain("ApplicationsToReview = applications.Count");
        source.Should().Contain("ListingsNeedingAttention = _db.RentalListings.Count");
        source.Should().Contain("ShowingsToday = _db.Appointments.Count");
        source.Should().Contain("UpcomingMoveIns = _db.LeaseManagements.Count");
        source.Should().Contain("UnreadConversations = conversations.Count");
    }

    [Fact]
    public void LeasingDetailsStayOnPurposeBuiltRoutesAndManagementRecordsAreDenied()
    {
        var listPage = ReadSource(
            "web", "src", "lib", "components", "leasing", "LeasingListPage.svelte");
        var routePolicy = ReadSource(
            "web", "src", "lib", "auth", "experience-policy.ts");
        var detailPage = ReadSource(
            "web", "src", "routes", "(protected)", "leasing", "[record]", "[id]", "+page.svelte");
        var applicationDetailPage = ReadSource(
            "web", "src", "routes", "(protected)", "leasing", "applications", "[id]", "+page.svelte");
        var applicationDetailComponent = ReadSource(
            "web", "src", "lib", "components", "records", "ApplicationDetail.svelte");
        var workspaceTypes = ReadSource(
            "web", "src", "lib", "api", "endpoints", "leasing-workspace.ts");
        var workspaceService = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeasingWorkspaceService.cs");

        listPage.Should().Contain("/leasing/rentals/");
        listPage.Should().Contain("/leasing/applications/");
        listPage.Should().Contain("/leasing/appointments/");
        listPage.Should().Contain("/leasing/conversations/");
        listPage.Should().Contain("/leasing/move-ins/");
        listPage.Should().NotContain("`/units/");
        listPage.Should().NotContain("`/properties/");
        listPage.Should().NotContain("`/messages");
        detailPage.Should().Contain("leasingWorkspace.rental(id)");
        detailPage.Should().Contain("<ListingTab unitId={detail.unitId} />");
        detailPage.Should().Contain("{#if detail.canViewApplications}");
        detailPage.Should().Contain("{#if detail.canViewShowings}");
        detailPage.Should().Contain("activeCapabilities.has(CAPABILITY.leasingAgreementsPrepare)");
        detailPage.Should().Contain("data-testid=\"leasing-rental-open-agreement-workspace\"");
        detailPage.Should().Contain("href={`/leases/${detail.leaseManagementId}`}");
        detailPage.Should().Contain("data-testid=\"leasing-move-in-open-agreement-workspace\"");
        detailPage.Should().Contain("href={`/leases/${detail.id}`}");
        detailPage.Should().NotContain("units.");
        detailPage.Should().NotContain("properties.");
        applicationDetailPage.Should().Contain("leasingWorkspace.application(id)");
        applicationDetailPage.Should().Contain("showApplicationActions={false}");
        applicationDetailPage.Should().Contain("showScreening={false}");
        applicationDetailPage.Should().Contain("showTenantLink={false}");
        applicationDetailPage.Should().Contain("applicationQueryScope=\"leasing\"");
        applicationDetailPage.Should().NotContain("applications.get");
        applicationDetailPage.Should().NotContain("applications.screening");
        applicationDetailComponent.Should().Contain("loadApplication = applications.get");
        applicationDetailComponent.Should().Contain("queryKey: ['application', applicationQueryScope, id]");
        applicationDetailComponent.Should().Contain("showScreening && !isNaN(id) && id > 0");
        listPage.Should().Contain("{#if item.canViewApplications || item.canViewShowings}");
        workspaceTypes.Should().Contain("canViewApplications: boolean;");
        workspaceTypes.Should().Contain("canViewShowings: boolean;");
        workspaceTypes.Should().Contain("leaseManagementId?: number | null;");
        workspaceTypes.Should().Contain("approvedTenantId?: number | null;");
        workspaceService.Should().Contain("AuthorizedApplications(scope, _time.GetUtcNow().UtcDateTime)");
        workspaceService.Should().Contain("ApprovedTenantId = application.ApprovedTenantId");
        workspaceService.Should().Contain("LeaseManagementId = unit.LeaseManagements");
        workspaceService.Should().Contain("management.CanceledAtUtc == null");
        workspaceService.Should().Contain("management.PossessionReturnedAtUtc == null");
        routePolicy.Should().Contain("prefix: '/units', experiences: ['Management']");
        routePolicy.Should().Contain("prefix: '/properties', experiences: ['Management']");
        routePolicy.Should().Contain("prefix: '/leases',");
        routePolicy.Should().Contain("CAPABILITY.leasingAgreementsPrepare");

        var listingTab = ReadSource(
            "web", "src", "lib", "components", "unit", "tabs", "ListingTab.svelte");
        listingTab.Should().Contain("let { unitId: propUnitId, dashboard }");
        listingTab.Should().Contain("const unitId = $derived(dashboard?.unit.id ?? propUnitId ?? 0);");
        detailPage.Should().NotContain("dashboard={");
    }

    [Fact]
    public void ManagementUnitAndPropertyDtosRequireTheManagementExperience()
    {
        var controllerBase = ReadSource(
            "RentalCommand.Api", "Controllers", "ManagementControllerBase.cs");
        var units = ReadSource("RentalCommand.Api", "Controllers", "UnitController.cs");
        var properties = ReadSource("RentalCommand.Api", "Controllers", "PropertyController.cs");

        controllerBase.Should().Contain("active.LastAuthorizedExperience != WorkspaceExperience.Management");
        units.Should().Contain("TryReadManagementScope");
        units.Should().Contain("if (!IsManagementExperience()) return Forbid();");
        units.Should().Contain(
            "active.LastAuthorizedExperience is not (WorkspaceExperience.Management or WorkspaceExperience.Leasing)");
        properties.Should().Contain("TryReadManagementScope");
        properties.Should().NotContain("TryReadWorkspaceScope");
    }

    [Fact]
    public void LeasingAppointmentDetailIncludesTheCalendarTypeContract()
    {
        typeof(LeasingAppointmentDetailResponse).GetProperty(nameof(LeasingAppointmentDetailResponse.Type))!
            .PropertyType.Should().Be(typeof(AppointmentType));
    }

    [Fact]
    public void LeasingApplicationDetailCarriesApprovedTenantForScopedPrepareMoveIn()
    {
        typeof(LeasingApplicationDetailResponse)
            .GetProperty(nameof(LeasingApplicationDetailResponse.ApprovedTenantId))!
            .PropertyType.Should().Be(typeof(int?));
    }

    [Fact]
    public void LeasingRentalDtosDistinguishHiddenFactsFromEmptyFacts()
    {
        typeof(LeasingRentalResponse).GetProperty(nameof(LeasingRentalResponse.LeaseManagementId))!
            .PropertyType.Should().Be(typeof(int?));
        typeof(LeasingRentalResponse).GetProperty(nameof(LeasingRentalResponse.CanViewApplications))!
            .PropertyType.Should().Be(typeof(bool));
        typeof(LeasingRentalResponse).GetProperty(nameof(LeasingRentalResponse.CanViewShowings))!
            .PropertyType.Should().Be(typeof(bool));
        typeof(LeasingRentalDetailResponse).GetProperty(nameof(LeasingRentalDetailResponse.LeaseManagementId))!
            .PropertyType.Should().Be(typeof(int?));
        typeof(LeasingRentalDetailResponse).GetProperty(nameof(LeasingRentalDetailResponse.CanViewApplications))!
            .PropertyType.Should().Be(typeof(bool));
        typeof(LeasingRentalDetailResponse).GetProperty(nameof(LeasingRentalDetailResponse.CanViewShowings))!
            .PropertyType.Should().Be(typeof(bool));
    }

    [Fact]
    public void LeasingConversationReplyUsesALightweightOnboardingScopedAccessCheck()
    {
        var controller = ReadSource(
            "RentalCommand.Api", "Controllers", "LeasingWorkspaceController.cs");
        var service = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeasingWorkspaceService.cs");

        var conversationService = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "ConversationService.cs");
        var atomicHandler = ReadSource(
            "RentalCommand.Data", "Conversations", "SendConversationMessageHandler.cs");

        controller.Should().Contain("_workspace.CanAccessConversationAsync(scope, id, ct)");
        controller.Should().NotContain("_workspace.GetConversationAsync(scope, id, ct) is null");
        controller.Should().Contain("PostMessageAuthorizedForCapabilityAsync");
        controller.Should().Contain("CapabilityKeys.LeasingOnboardingManage, ct");
        service.Should().Contain("AnyAsync(conversation => conversation.Id == id, ct)");
        service.Should().Contain("_db, scope, [CapabilityKeys.LeasingOnboardingManage], now");
        conversationService.Should().Contain("requiredCapabilityKey");
        atomicHandler.Should().Contain("profileCapability.CapabilityDefinition.Key == access.RequiredCapabilityKey");
    }

    [Fact]
    public void AuthorizedLandlordConversationWritesLockAuthorityBeforeTheConversation()
    {
        var handler = ReadSource(
            "RentalCommand.Data", "Conversations", "SendConversationMessageHandler.cs");

        var sessionLock = handler.IndexOf(
            "AtomicLockResource.AuthSession, managementAccess.SessionId", StringComparison.Ordinal);
        var contextLock = handler.IndexOf(
            "AtomicLockResource.WorkspaceAccessContext, managementAccess.AccessContextId",
            StringComparison.Ordinal);
        var conversationLock = handler.IndexOf(
            "AtomicLockResource.Conversation, conversationId", StringComparison.Ordinal);

        sessionLock.Should().BeGreaterThan(0);
        contextLock.Should().BeGreaterThan(sessionLock);
        conversationLock.Should().BeGreaterThan(contextLock);
    }

    [Fact]
    public void OwnerDistributionControlsFollowExactMutationCapabilities()
    {
        var page = ReadSource(
            "web", "src", "routes", "(protected)", "owners-report", "+page.svelte");

        page.Should().Contain("hasCapability('money.disbursements.manage')");
        page.Should().Contain("hasCapability('money.reconciliation.destructive')");
        page.Should().Contain("{#if canCreateDistribution}");
        page.Should().Contain("{#if canDeleteDistribution}");
    }

    private static string ReadSource(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));
    }
}
