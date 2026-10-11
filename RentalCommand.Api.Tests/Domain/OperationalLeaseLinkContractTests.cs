using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Api.Tests.Domain;

public sealed class OperationalLeaseLinkContractTests
{
    [Fact]
    public void Operational_entities_expose_only_precise_canonical_lease_links()
    {
        typeof(WorkOrder).GetProperty("LeaseId").Should().BeNull();
        typeof(WorkOrder).GetProperty(nameof(WorkOrder.LeaseManagementId))!.PropertyType.Should().Be(typeof(int?));
        typeof(Appointment).GetProperty("LeaseId").Should().BeNull();
        typeof(Appointment).GetProperty(nameof(Appointment.LeaseManagementId))!.PropertyType.Should().Be(typeof(int?));
        typeof(Appointment).GetProperty(nameof(Appointment.RentalApplicationId))!.PropertyType.Should().Be(typeof(int?));
        typeof(Inspection).GetProperty("LeaseId").Should().BeNull();
        typeof(Inspection).GetProperty(nameof(Inspection.LeaseManagementId))!.PropertyType.Should().Be(typeof(int?));
        typeof(Inspection).GetProperty(nameof(Inspection.LeaseAgreementId))!.PropertyType.Should().Be(typeof(int?));
        typeof(EvictionCase).GetProperty("LeaseId").Should().BeNull();
        typeof(EvictionCase).GetProperty(nameof(EvictionCase.LeaseManagementId))!.PropertyType.Should().Be(typeof(int));
        typeof(EvictionCase).GetProperty(nameof(EvictionCase.LeaseAgreementId))!.PropertyType.Should().Be(typeof(int?));
        typeof(EvictionCase).GetProperty("TenantId").Should().BeNull();
    }

    [Fact]
    public void Eviction_respondents_are_explicit_lease_management_party_links()
    {
        typeof(EvictionCaseRespondent).GetProperty(nameof(EvictionCaseRespondent.LeaseManagementPartyId))!
            .PropertyType.Should().Be(typeof(int));
        typeof(CreateEvictionCaseRequest).GetProperty(nameof(CreateEvictionCaseRequest.RespondentLeaseManagementPartyIds))
            .Should().NotBeNull();
    }

    [Fact]
    public void Work_order_scan_contract_does_not_accept_a_legacy_lease_id()
    {
        typeof(ScanWorkOrderTargetData).GetProperty("LeaseId").Should().BeNull();
        typeof(ScanWorkOrderTargetData).GetProperty(nameof(ScanWorkOrderTargetData.LeaseManagementId))
            .Should().NotBeNull();
    }
}
