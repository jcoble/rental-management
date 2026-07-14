using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class NotificationRouteContractTests
{
    [Fact]
    public void NotificationAreas_UseSeparateCanonicalControllerRoutes()
    {
        RouteOf<MyAlertsController>().Should().Be("api/v1/my-alerts");
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
        typeof(TenantNoticePoliciesController)
            .GetMethods()
            .Select(method => method.GetCustomAttribute<HttpPostAttribute>()?.Template)
            .Where(template => template is not null)
            .Should().BeEquivalentTo(
                "templates/{systemKey}/versions",
                "templates/{systemKey}/restore-default");
    }

    private static string? RouteOf<T>() =>
        typeof(T).GetCustomAttribute<RouteAttribute>()?.Template;
}
