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

        writer.Supports(ScanConfirmationTargetKind.LeaseAgreement).Should().BeTrue();
        typeof(ScanLeaseTargetData).GetProperty("LeaseId").Should().BeNull();
    }

    [Fact]
    public void Every_agreement_requires_one_immutable_document_source_version()
    {
        typeof(LeaseAgreement).GetProperty(nameof(LeaseAgreement.DocumentSourceVersionId))!
            .PropertyType.Should().Be(typeof(int));
        typeof(LeaseAgreement).GetProperty("DocumentTemplateId").Should().BeNull();
        typeof(LeaseAgreement).GetProperty("DocumentTemplateVersion").Should().BeNull();
    }

    [Fact]
    public void Unsigned_scan_uses_the_supplied_source_when_no_custom_template_is_selected()
    {
        var target = LeaseTarget(
            LeaseScanReviewDisposition.NeedsSignatures,
            documentTemplateId: null);

        CanonicalLeaseScanConfirmationWriter.SelectDocumentSource(target)
            .Should().Be(CanonicalLeaseScanDocumentSourceKind.BuiltInRenderer);
    }

    [Fact]
    public void Signed_scan_preserves_the_uploaded_document_instead_of_using_a_template()
    {
        var target = LeaseTarget(
            LeaseScanReviewDisposition.AlreadyFullySigned,
            documentTemplateId: null);

        CanonicalLeaseScanConfirmationWriter.SelectDocumentSource(target)
            .Should().Be(CanonicalLeaseScanDocumentSourceKind.ImportedExternalDocument);
    }

    [Fact]
    public void Unsigned_scan_with_custom_template_freezes_an_authored_snapshot()
    {
        var target = LeaseTarget(
            LeaseScanReviewDisposition.NeedsSignatures,
            documentTemplateId: 42);

        CanonicalLeaseScanConfirmationWriter.SelectDocumentSource(target)
            .Should().Be(CanonicalLeaseScanDocumentSourceKind.AuthoredTemplateSnapshot);
    }

    [Fact]
    public void Non_positive_custom_template_id_is_not_treated_as_the_supplied_source()
    {
        var target = LeaseTarget(
            LeaseScanReviewDisposition.NeedsSignatures,
            documentTemplateId: 0);

        var act = () => CanonicalLeaseScanConfirmationWriter.SelectDocumentSource(target);

        act.Should().Throw<ScanConfirmationValidationException>();
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

    private static ScanLeaseTargetData LeaseTarget(
        LeaseScanReviewDisposition disposition,
        int? documentTemplateId) => new(
            PropertyId: 1,
            UnitId: 2,
            TenantId: 3,
            TenantName: null,
            TenantEmail: null,
            TenantPhone: null,
            TenantEmergencyContact: null,
            PropertyName: null,
            PropertyType: null,
            RentalStructure: null,
            PropertyAddress: null,
            PropertyCity: null,
            PropertyState: null,
            PropertyPostalCode: null,
            UnitNumber: null,
            UnitBedrooms: null,
            UnitBathrooms: null,
            UnitSquareFeet: null,
            LeaseNumber: null,
            StartDate: new DateTime(2026, 8, 1),
            EndDate: new DateTime(2027, 7, 31),
            MonthlyRent: 1_200m,
            SecurityDeposit: 1_200m,
            LateFee: 50m,
            RentDueDay: 1,
            ReviewDisposition: disposition,
            DocumentTemplateId: documentTemplateId);
}
