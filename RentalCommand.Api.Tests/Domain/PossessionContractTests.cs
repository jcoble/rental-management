using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

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

    [Fact]
    public void Confirm_move_in_is_one_atomic_command_and_derives_money_identity_server_side()
    {
        var route = typeof(LeaseManagementController)
            .GetMethod(nameof(LeaseManagementController.ConfirmMoveIn))!
            .GetCustomAttribute<HttpPostAttribute>();

        route.Should().NotBeNull();
        route!.Template.Should().Be("{leaseManagementId:int}/confirm-move-in");
        typeof(ConfirmMoveInHandler)
            .Should().Implement<IAtomicCommandHandler<ConfirmMoveInCommand, ConfirmMoveInResult>>();
        typeof(ConfirmMoveInHandler)
            .Should().Implement<IAtomicReplayAuthorizer<ConfirmMoveInCommand>>();
        typeof(ConfirmMoveInRequest).GetProperty("TenantAccountId").Should().BeNull();
        typeof(ConfirmMoveInRequest).GetProperty("SecurityDepositAccountId").Should().BeNull();
        typeof(ConfirmMoveInRequest).GetProperty("Amount").Should().BeNull();
    }
}
