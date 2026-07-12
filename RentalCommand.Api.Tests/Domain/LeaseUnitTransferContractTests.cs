using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseUnitTransferContractTests
{
    [Fact]
    public void Transfer_uses_typed_cross_relationship_and_financial_provenance()
    {
        LeaseAgreementChangeType.Transfer.ToString().Should().Be("Transfer");
        TenantLedgerEntryType.TransferIn.ToString().Should().Be("TransferIn");
        TenantLedgerEntryType.TransferOut.ToString().Should().Be("TransferOut");
        SecurityDepositEntryType.TransferIn.ToString().Should().Be("TransferIn");
        SecurityDepositEntryType.TransferOut.ToString().Should().Be("TransferOut");
    }

    [Fact]
    public async Task Immediate_destination_possession_requires_an_explicit_exception_reason()
    {
        var command = ValidCommand() with
        {
            GiveDestinationPossessionNow = true,
            PossessionAgreementExceptionReason = null,
        };

        Func<Task> act = async () => await new TransferLeaseManagementHandler()
            .HandleAsync(command, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Planned_destination_cannot_carry_an_unused_possession_exception()
    {
        var command = ValidCommand() with
        {
            GiveDestinationPossessionNow = false,
            PossessionAgreementExceptionReason = "Not actually used.",
        };

        Func<Task> act = async () => await new TransferLeaseManagementHandler()
            .HandleAsync(command, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static TransferLeaseManagementCommand ValidCommand() => new(
        PortfolioId: 1,
        SourceLeaseManagementId: 10,
        SourceUnitId: 100,
        DestinationUnitId: 200,
        CreatedByUserId: 7,
        AuthSessionId: Guid.NewGuid(),
        AccessContextId: 8,
        ExpectedAccessRevision: 1,
        TransferPublicId: Guid.NewGuid(),
        EffectiveOn: new DateOnly(2026, 7, 12),
        PlannedDestinationPossessionAtUtc: null,
        GiveDestinationPossessionNow: false,
        PossessionAgreementExceptionReason: null,
        DestinationDocumentTemplateId: 50,
        DestinationDocumentTemplateVersion: 1,
        CarryTenantBalance: true,
        CarrySecurityDeposit: true,
        TransferReason: "Household moved to another Unit.",
        DeliveryIdempotencyKey: "unit-transfer:test");
}
