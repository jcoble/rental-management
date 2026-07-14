using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class LeasingExperienceContractTests
{
    [Fact]
    public void LeasingWorkspaceExposesOnlyPurposeBuiltReadRoutes()
    {
        var routes = typeof(LeasingWorkspaceController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .Cast<HttpGetAttribute>())
            .Select(attribute => attribute.Template)
            .ToArray();

        routes.Should().BeEquivalentTo(
            "today", "pipeline/page", "rentals/page", "calendar/page", "inbox/page");
        typeof(LeasingWorkspaceController).BaseType.Should().Be(typeof(ManagementControllerBase));
    }

    [Fact]
    public void LeasingProjectionsRemainDatabaseSideAndCapabilityScoped()
    {
        var source = ReadSource(
            "RentalCommand.Api", "Services", "Domain", "LeasingWorkspaceService.cs");

        source.Should().Contain(nameof(CapabilityKeys.LeasingApplicationsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingListingsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingShowingsManage));
        source.Should().Contain(nameof(CapabilityKeys.LeasingOnboardingManage));
        source.Should().Contain("WhereAuthorized");
        source.Should().Contain("CountAsync");
        source.Should().Contain("Skip(query.NormalizedSkip).Take(query.NormalizedTake)");
        source.Should().NotContain("AsEnumerable");
        source.Should().NotContain("GroupBy(");
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
