using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Auth;

public sealed class NotificationRouteContractTests
{
    [Fact]
    public void NotificationAreas_UseSeparateCanonicalControllerRoutes()
    {
        RouteOf<MyAlertsController>().Should().Be("api/v1/my-alerts");
        RouteOf<AutomationSettingsController>().Should().Be("api/v1/automation-settings");
        RouteOf<TeamRoutingController>().Should().Be("api/v1/team-routing");
        RouteOf<TenantNoticePoliciesController>().Should().Be("api/v1/tenant-notices");
    }

    [Fact]
    public void PersonalAlerts_DoNotRequireWorkspaceNotificationManagement()
    {
        typeof(MyAlertsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(AutomationSettingsController))]
    [InlineData(typeof(TeamRoutingController))]
    [InlineData(typeof(TenantNoticePoliciesController))]
    public void AdministrativeAreas_RequireNotificationManagement(Type controllerType)
    {
        var authorization = controllerType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorization.Roles.Should().BeNullOrWhiteSpace();
        authorization.Policy.Should().Be(
            CapabilityPolicy.Prefix + CapabilityKeys.NotificationsManage);
    }

    [Fact]
    public void TenantNoticeController_HasNoManualBlankTemplateSeedRoute()
    {
        var postRoutes = typeof(TenantNoticePoliciesController)
            .GetMethods()
            .Select(method => method.GetCustomAttribute<HttpPostAttribute>()?.Template)
            .Where(template => template is not null);

        postRoutes.Should().Contain("templates/{systemKey}/versions");
        postRoutes.Should().Contain("templates/{systemKey}/restore-default");
        postRoutes.Should().NotContain("tenant-notices/templates/seed");
        postRoutes.Should().NotContain("templates/seed");
    }

    [Fact]
    public void TenantNoticeController_ExposesRecipientPreviewAndMergeFieldHelp()
    {
        var getRoutes = typeof(TenantNoticePoliciesController)
            .GetMethods()
            .Select(method => method.GetCustomAttribute<HttpGetAttribute>()?.Template)
            .Where(template => template is not null);

        getRoutes.Should().Contain("{automationKey}/recipients");
        getRoutes.Should().Contain("templates/{systemKey}/merge-fields");
    }

    [Fact]
    public void AutomationSettingsController_RestoresLateFeeControlsWithoutRentChargeToggle()
    {
        typeof(AutomationSettingsController).GetMethods()
            .Single(method => method.Name == nameof(AutomationSettingsController.GetLateFees))
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("late-fees");
        typeof(AutomationSettingsController).GetMethods()
            .Single(method => method.Name == nameof(AutomationSettingsController.UpdateLateFees))
            .GetCustomAttribute<HttpPutAttribute>()!.Template.Should().Be("late-fees");

        typeof(UpdateLateFeeAutomationSettingsRequest)
            .GetProperties()
            .Select(property => property.Name)
            .Should().BeEquivalentTo(nameof(UpdateLateFeeAutomationSettingsRequest.EnableLateFees),
                nameof(UpdateLateFeeAutomationSettingsRequest.LateFeeGraceDays));
        typeof(UpdateLateFeeAutomationSettingsRequest).GetProperty("EnableRentCharges").Should().BeNull();
    }

    [Fact]
    public void NotificationReadContract_EmitsTypedIntentAndNeverRawUrl()
    {
        var response = new NotificationResponse
        {
            Id = 7,
            Type = "TenantMessage",
            Title = "New message",
            Message = "Please review",
            Severity = "Info",
            NavigationIntent = NavigationIntentDtoMapper.ToDto(new NavigationIntent(
                NavigationExperience.Tenant,
                NavigationDestination.Message,
                AccessContextId: 12,
                AccessRevision: 4,
                ResourceKind: "Conversation",
                ResourceId: 91,
                ParentResourceKind: null,
                ParentResourceId: null,
                ChildResourceKind: null,
                ChildResourceId: null,
                NavigationAction.Open,
                new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc),
                NavigationDestination.Home)),
            CreatedAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc),
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, options));
        var root = json.RootElement;
        root.TryGetProperty("actionUrl", out _).Should().BeFalse();
        var intent = root.GetProperty("navigationIntent");
        intent.GetProperty("experience").GetString().Should().Be("Tenant");
        intent.GetProperty("destination").GetString().Should().Be("Message");
        intent.GetProperty("accessContextId").GetInt32().Should().Be(12);
        intent.GetProperty("accessRevision").GetInt64().Should().Be(4);
        intent.GetProperty("resource").GetProperty("kind").GetString().Should().Be("Conversation");
        intent.GetProperty("resource").GetProperty("id").GetInt32().Should().Be(91);
        intent.GetProperty("action").GetString().Should().Be("Open");
        intent.GetProperty("expiresAtUtc").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        intent.GetProperty("fallbackDestination").GetString().Should().Be("Home");
        typeof(NotificationResponse).GetProperty("ActionUrl").Should().BeNull();
        typeof(CreateBroadcastNotificationRequest).GetProperty("ActionUrl").Should().BeNull();
    }

    [Fact]
    public void TenantNoticeDelivery_UsesTenantLedgerEntryIntentForLedgerBackedPaymentNotices()
    {
        var source = Read("RentalCommand.Api", "Services", "Domain", "AtomicNoticeDeliveryRule.cs");

        source.Should().Contain("NoticeNavigationDestination(draft)");
        source.Should().Contain("NavigationDestination.TenantLedgerEntry");
        source.Should().Contain("NoticeNavigationParentResourceKind(draft)");
        source.Should().Contain("nameof(TenantAccount)");
        source.Should().Contain("PushNavigationIntent(draft, destination, now)");
        source.Should().Contain("ReconcileApprovalNotificationIntentAsync(");
        source.Should().Contain("action = NavigationAction.Open.ToString()");
        source.Should().Contain("draft.Status == \"Draft\" || draft.Status == \"Approved\"");
        source.Should().Contain("entry.Id == draft.TenantLedgerEntryId.Value");
        source.Should().Contain("entry.TenantAccountId == draft.TenantAccountId");
        source.Should().NotContain("NavigationDestination = NavigationDestination.Message,\n                    NavigationAccessContextId = destination.NavigationAccessContextId");
    }

    [Fact]
    public void TenantNoticeDeliveryReplay_ValidatesLedgerBackedNotificationGraph()
    {
        var source = Read("RentalCommand.Data", "Notifications", "TenantNoticeDraftSetStore.cs");

        source.Should().Contain("rendered_draft AS MATERIALIZED");
        source.Should().Contain("ReconcileApprovalNotificationIntentSql");
        source.Should().Contain("UPDATE \"Notifications\" AS notification");
        source.Should().Contain("INNER JOIN \"TenantLedgerEntries\" AS ledger");
        source.Should().Contain("ledger.\"TenantAccountId\" = draft.\"TenantAccountId\"");
        source.Should().Contain("HasValidLedgerBinding");
        source.Should().Contain("ValidateApprovalRecoveryCandidateAsync");
        source.Should().Contain("allow_stale_message_notification");
        source.Should().Contain("notification.\"NavigationDestination\" =");
        source.Should().Contain("CASE WHEN draft.\"TenantLedgerEntryId\" IS NULL THEN 'Message' ELSE 'TenantLedgerEntry' END");
        source.Should().Contain("notification.\"NavigationDestination\" = 'Message'");
        source.Should().Contain("notification.\"NavigationParentResourceKind\" = 'TenantAccount'");
        source.Should().Contain("notification.\"RelatedEntityType\" =");
        source.Should().Contain("notification.\"RelatedEntityType\" = 'Conversation'");
    }

    [Fact]
    public void NavigationDestination_IsAClosedTypedSet()
    {
        Enum.GetNames<NavigationDestination>().Should().BeEquivalentTo(
            "Home", "Notifications", "Rentals", "Owners", "Money", "Work", "Inbox",
            "UnitSummary", "UnitTenantLease", "UnitMoney", "UnitMaintenance", "UnitRecords",
            "TenantLedgerEntry", "Expense", "ScanDraft", "Message", "WorkOrder",
            "TechnicianWork", "LeasingRental", "LeasingApplication", "LeasingAppointment",
            "LeasingConversation", "LeasingMoveIn", "TenantAccount");
    }

    [Fact]
    public void NotificationController_ExposesAuthorizedListAndDetailReads()
    {
        var methods = typeof(NotificationsController).GetMethods();
        methods.Single(method => method.Name == nameof(NotificationsController.List))
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().BeNull();
        methods.Single(method => method.Name == nameof(NotificationsController.Get))
            .GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{id:int}");
    }

    [Fact]
    public void DeviceRegistration_IsAuditedAndScopedToOneTokenPerWorkspace()
    {
        typeof(DeviceToken).Should().BeAssignableTo<IAuditable>();
        typeof(DeviceToken).Should().BeAssignableTo<IPortfolioScoped>();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=rentalcommand_model_contract")
            .Options;
        using var db = new RentalCommandDbContext(options);
        var entity = db.Model.FindEntityType(typeof(DeviceToken));
        entity.Should().NotBeNull();
        var uniqueIndexes = entity!.GetIndexes()
            .Where(index => index.IsUnique)
            .Select(index => index.Properties.Select(property => property.Name).ToArray())
            .ToArray();
        uniqueIndexes.Should().ContainSingle(properties => properties.SequenceEqual(new[]
        {
            nameof(DeviceToken.PortfolioId),
            nameof(DeviceToken.Token),
        }));
        uniqueIndexes.Any(properties => properties.SequenceEqual(new[] { nameof(DeviceToken.Token) }))
            .Should().BeFalse();
    }

    private static string? RouteOf<T>() =>
        typeof(T).GetCustomAttribute<RouteAttribute>()?.Template;

    private static string Read(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run below the repository root");
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(path).ToArray()));
    }
}
