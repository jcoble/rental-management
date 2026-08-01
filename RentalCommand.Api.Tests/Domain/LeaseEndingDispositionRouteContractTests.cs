using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseEndingDispositionRouteContractTests
{
    [Fact]
    public void Ending_disposition_is_an_explicit_idempotent_lease_management_command()
    {
        var method = typeof(LeaseManagementController)
            .GetMethod(nameof(LeaseManagementController.RecordEndingDisposition))!;

        method.GetCustomAttribute<HttpPostAttribute>()!.Template
            .Should().Be("{leaseManagementId:int}/ending-disposition");
        method.GetParameters().Should().Contain(parameter =>
            parameter.GetCustomAttributes<FromHeaderAttribute>()
                .Any(attribute => attribute.Name == "Idempotency-Key"));
        typeof(RecordLeaseEndingDispositionHandler).Should()
            .Implement<IAtomicCommandHandler<RecordLeaseEndingDispositionCommand, RecordLeaseEndingDispositionResult>>();
    }

    [Fact]
    public void Detail_exposes_current_upcoming_and_ending_facts_without_a_mutable_agreement_request()
    {
        typeof(LeaseManagementSummaryResponse)
            .GetProperty(nameof(LeaseManagementSummaryResponse.UpcomingAgreementNumber))
            .Should().NotBeNull();
        typeof(LeaseManagementSummaryResponse)
            .GetProperty(nameof(LeaseManagementSummaryResponse.EndingDispositionDecidedAtUtc))
            .Should().NotBeNull();
        typeof(RecordLeaseEndingDispositionRequest).GetProperty("AgreementStatus")
            .Should().BeNull();
        typeof(RecordLeaseEndingDispositionRequest).GetProperty("AgreementId")
            .Should().BeNull();
    }
}
