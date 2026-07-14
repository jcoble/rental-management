using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
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
}
