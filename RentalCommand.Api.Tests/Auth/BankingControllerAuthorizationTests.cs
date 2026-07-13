using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public class BankingControllerAuthorizationTests
{
    [Fact]
    public void BankingController_RequiresCanonicalBankConnectionCapability()
    {
        var declared = typeof(BankingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        declared.Roles.Should().BeNullOrWhiteSpace();
        declared.Policy.Should().Be(CapabilityPolicy.Prefix + CapabilityKeys.BankConnectionsManage);
    }
}
