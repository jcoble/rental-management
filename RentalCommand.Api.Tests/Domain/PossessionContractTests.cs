using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PossessionContractTests
{
    [Fact]
    public void Return_possession_command_contract_has_no_client_supplied_effective_date()
    {
        typeof(ReturnPossessionRequest).GetProperty("EffectiveOn").Should().BeNull();
        typeof(ReturnPossessionCommand).GetProperty("EffectiveOn").Should().BeNull();
    }

    [Fact]
    public void Return_possession_context_keeps_access_flat_and_out_of_ordinary_party_detail()
    {
        typeof(LeaseManagementPartyResponse).GetProperty("ActiveTenantUserAccesses").Should().BeNull();
        typeof(ReturnPossessionContextResponse).GetProperty("ActiveTenantUserAccesses")
            .Should().NotBeNull();
    }
}
