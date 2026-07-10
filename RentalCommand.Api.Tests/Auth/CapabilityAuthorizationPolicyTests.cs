using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class CapabilityAuthorizationPolicyTests
{
    [Fact]
    public async Task DynamicPolicy_UsesStableCapabilityRequirement_NotRoleAliases()
    {
        var services = new ServiceCollection();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, CapabilityAuthorizationPolicyProvider>();
        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(
            CapabilityPolicy.For(CapabilityKeys.MoneyPaymentsManage));

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<CapabilityRequirement>()
            .Should().ContainSingle(requirement =>
                requirement.CapabilityKey == CapabilityKeys.MoneyPaymentsManage);
        policy.Requirements.OfType<RolesAuthorizationRequirement>().Should().BeEmpty();
    }
}
