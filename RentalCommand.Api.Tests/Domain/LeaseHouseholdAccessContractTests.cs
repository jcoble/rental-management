using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseHouseholdAccessContractTests
{
    [Fact]
    public void Household_mutations_are_idempotent_relationship_scoped_commands()
    {
        var controller = typeof(LeaseManagementController);
        foreach (var methodName in new[]
        {
            nameof(LeaseManagementController.AddEffectiveParty),
            nameof(LeaseManagementController.ChangeEffectivePartyRole),
            nameof(LeaseManagementController.EndEffectiveParty),
            nameof(LeaseManagementController.GrantTenantUserAccess),
            nameof(LeaseManagementController.RevokeTenantUserAccess),
        })
        {
            controller.GetMethod(methodName)!.GetParameters().Should().Contain(parameter =>
                parameter.GetCustomAttributes<FromHeaderAttribute>()
                    .Any(attribute => attribute.Name == "Idempotency-Key"));
        }

        typeof(GrantTenantUserAccessRequest).GetProperty("ApplicationUserId").Should().BeNull();
        typeof(LeaseManagementPartyResponse).GetProperty("CanGrantTenantPortalAccess").Should().NotBeNull();
    }

    [Fact]
    public void Tenant_controller_has_no_tenant_wide_access_mutation()
    {
        typeof(TenantController).GetMethods().Should().NotContain(method =>
            method.GetCustomAttributes<HttpPostAttribute>().Any(attribute =>
                attribute.Template == "{id:int}/portal-access" ||
                attribute.Template == "{id:int}/portal-invite"));
    }

    [Fact]
    public void Tenant_activation_uses_passwordless_identity_and_a_non_credential_invitation()
    {
        typeof(ApplicationUser).Should().BeDerivedFrom<IdentityUser<int>>();
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "RentalCommand.Data", "Leasing", "LeasePartyAccessRules.cs"));

        source.Should().Contain("\"TenantIdentityEmail\"");
        source.Should().Contain("EmailConfirmed = false");
        source.Should().Contain("/activate-team?token=");
        source.Should().Contain("WorkspaceInvitation");
        source.Should().Contain("RoleProfileKeys.TenantPortal");
        source.Should().Contain("var changedAtUtc = times.EffectiveNowUtc");
        source.Should().Contain("var tenantSecurityNowUtc = await context.ReadDatabaseClockUtcAsync(ct)");
        source.Should().Contain("context.UseDatabaseWallClockForAudit(changedAtUtc)");
        source.Should().Contain("EffectiveFromUtc = tenantSecurityNowUtc");
        source.Should().Contain("CreatedAtUtc = changedAtUtc");
        source.Should().Contain("ExpiresAtUtc = changedAtUtc.AddDays(7)");
        source.Should().Contain(
            "relationship.Parties.FirstOrDefault(party => party.Id == command.PartyId" + Environment.NewLine +
            "                    && (party.EffectiveThrough == null || party.EffectiveThrough >= currentDate))");
        source.Should().NotContain("CreateTemporaryPassword");
        source.Should().NotContain("Temporary password:");
        source.Should().NotContain("HashPassword(user");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
    }
}
