using FluentAssertions;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Scanning;

namespace RentalCommand.Data.Tests.Scanning;

public sealed class CanonicalLeaseScanImportContractTests
{
    [Fact]
    public void Production_writer_activates_the_canonical_lease_target()
    {
        var writer = new ProductionScanConfirmationTargetWriter();

        writer.Supports(ScanConfirmationTargetKind.Lease).Should().BeTrue();
        typeof(ScanLeaseTargetData).GetProperty("LeaseId").Should().BeNull();
    }

    [Fact]
    public void External_executed_agreement_does_not_require_fake_template_provenance()
    {
        typeof(LeaseAgreement).GetProperty(nameof(LeaseAgreement.DocumentTemplateId))!
            .PropertyType.Should().Be(typeof(int?));
        typeof(LeaseAgreement).GetProperty(nameof(LeaseAgreement.DocumentTemplateVersion))!
            .PropertyType.Should().Be(typeof(int?));
    }

    [Fact]
    public void Maintenance_technician_has_no_capability_that_can_import_an_agreement()
    {
        var technician = AccessCatalog.Roles.Single(role =>
            role.Key == RoleProfileKeys.MaintenanceTechnician);
        var technicianCapabilityKeys = AccessCatalog.CapabilityIdsByRole[technician.Id]
            .Join(
                AccessCatalog.Capabilities,
                capabilityId => capabilityId,
                capability => capability.Id,
                (_, capability) => capability.Key)
            .ToArray();

        technicianCapabilityKeys.Should().NotContain(CapabilityKeys.RentalsManage);
        technicianCapabilityKeys.Should().NotContain(CapabilityKeys.LeasingAgreementsPrepare);
    }
}
