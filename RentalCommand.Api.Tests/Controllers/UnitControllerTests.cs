using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Tests.Controllers;

public sealed class UnitControllerTests
{
    [Fact]
    public async Task Get_NonexistentUnit_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.Get(999_999, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Dashboard_NonexistentUnit_ReturnsNotFound()
    {
        var controller = CreateController();

        var result = await controller.Dashboard(999_999, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    private static UnitController CreateController()
    {
        var authorization = new Mock<IWorkspaceAuthorizationEvaluator>(MockBehavior.Strict);
        authorization.Setup(x => x.HasCapabilityAsync(
                It.IsAny<ActiveAccessContext>(),
                CapabilityKeys.RentalsRead,
                It.IsAny<UnitCapabilityAuthorizationTarget>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var services = new ServiceCollection()
            .AddSingleton(authorization.Object)
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            Guid.NewGuid(), 7, 11, 42, 1, WorkspaceExperience.Management, 13, WorkspaceExperience.Management);

        return new UnitController(
            Mock.Of<IUnitService>(),
            Mock.Of<IUnitDashboardService>(),
            Mock.Of<IListingWorkspaceService>(),
            authorization.Object,
            TimeProvider.System,
            Options.Create(new UploadSettings()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
