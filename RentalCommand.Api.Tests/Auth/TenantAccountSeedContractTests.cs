using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Tests.Auth;

public class TenantAccountSeedContractTests
{
    [Fact]
    public void CreateTeamMemberRequest_ExposesTenantLinkForPortalUsers()
    {
        typeof(CreateTeamMemberRequest)
            .GetProperty("TenantId")
            .Should()
            .NotBeNull("tenant portal logins need a tenantId claim to see their in-app messages");
    }

    [Fact]
    public void SeedSettings_ExposeTenantPortalPassword()
    {
        typeof(SeedSettings)
            .GetProperty("TenantPassword")
            .Should()
            .NotBeNull("demo-seeded tenants need Identity credentials for portal login smoke tests");
    }
}
