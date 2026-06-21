using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using RentalCommand.Api.Controllers;

namespace RentalCommand.Api.Tests.Auth;

public class BankingControllerAuthorizationTests
{
    [Fact]
    public void BankingController_IsRestrictedToAdminAndManagerRoles()
    {
        // Banking declares its OWN tighter gate (Admin,Manager). It now also inherits the staff-role gate
        // from ManagementControllerBase (Admin,Manager,Agent,Owner); the two [Authorize] attributes combine
        // with AND, so the effective access is the intersection — still exactly Admin,Manager. Assert on the
        // DECLARED attribute (inherit: false) so this stays a precise statement of Banking's own intent and
        // isn't confused by the inherited base attribute.
        var declared = typeof(BankingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => !string.IsNullOrWhiteSpace(a.Roles));

        declared.Should().NotBeNull();
        declared!.Roles.Should().Be("Admin,Manager");

        // And the effective (own + inherited) restriction must never admit a Tenant.
        var effectiveRoleLists = typeof(BankingController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Where(a => !string.IsNullOrWhiteSpace(a.Roles))
            .Select(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        effectiveRoleLists.Should().OnlyContain(roles => !roles.Contains("Tenant"));
    }
}
