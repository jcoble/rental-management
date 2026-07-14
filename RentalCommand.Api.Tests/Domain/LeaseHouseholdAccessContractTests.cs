using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
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
                parameter.GetCustomAttribute<FromHeaderAttribute>() is { Name: "Idempotency-Key" });
        }

        typeof(GrantTenantUserAccessHandler)
            .Should().Implement<IAtomicReplayAuthorizer<GrantTenantUserAccessCommand>>();
        typeof(GrantTenantUserAccessRequest).GetProperty("ApplicationUserId").Should().BeNull();
    }

    [Fact]
    public void Tenant_controller_has_no_tenant_wide_access_mutation()
    {
        typeof(TenantController).GetMethods().Should().NotContain(method =>
            method.GetCustomAttribute<HttpPostAttribute>()?.Template is
                "{id:int}/portal-access" or "{id:int}/portal-invite");
    }

    [Fact]
    public void Tenant_activation_uses_passwordless_identity_and_a_non_credential_invitation()
    {
        typeof(ApplicationUser).Should().BeDerivedFrom<IdentityUser<int>>();
        Enum.IsDefined(AtomicLockResource.TenantIdentityEmail).Should().BeTrue();

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "RentalCommand.Data", "Leasing", "LeasePartyAccessCommandHandlers.cs"));

        source.Should().Contain("AtomicLockResource.TenantIdentityEmail");
        source.Should().Contain("EmailConfirmed = false");
        source.Should().Contain("/forgot-password?email=");
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
