using FluentAssertions;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Api.Tests.Scanning;

public sealed class ScanCaptureContextContractTests
{
    private static readonly string[] CanonicalRecordProperties =
    [
        "PropertyId",
        "UnitId",
        "LeaseManagementId",
        "LeaseAgreementId",
        "TenantAccountId",
        "TenantLedgerEntryId",
        "WorkOrderId",
        "ApplicationId",
        "RentalListingId",
    ];

    [Fact]
    public void CaptureContracts_ExposeExplicitCanonicalRecordIdsOnly()
    {
        typeof(ScanCaptureContextData).GetProperties().Select(property => property.Name)
            .Should().Contain(CanonicalRecordProperties)
            .And.NotContain(["FocusedRecordKind", "FocusedRecordId"]);
        typeof(ScanCaptureContextDto).GetProperties().Select(property => property.Name)
            .Should().Contain(CanonicalRecordProperties)
            .And.NotContain(["FocusedRecordKind", "FocusedRecordId"]);
    }

    [Fact]
    public void ScanDraft_PersistsExplicitCanonicalForeignKeysOnly()
    {
        var properties = typeof(ScanDraft).GetProperties().Select(property => property.Name).ToArray();

        properties.Should().Contain(CanonicalRecordProperties.Select(name => $"Capture{name}"));
        properties.Should().NotContain(["CaptureFocusedRecordKind", "CaptureFocusedRecordId"]);
    }
}
