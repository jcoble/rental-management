using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Controllers;

namespace RentalCommand.Api.Tests.Auth;

public class BankingControllerAuthorizationTests
{
    [Fact]
    public void BankingController_IsRestrictedToAdminAndManagerRoles()
    {
        var authorize = typeof(BankingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => !string.IsNullOrWhiteSpace(a.Roles));

        authorize.Should().NotBeNull();
        authorize!.Roles.Should().Be("Admin,Manager");
    }
}
