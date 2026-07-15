using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class PrepareMoveInValidationTests
{
    [Fact]
    public void Explicit_unit_with_new_household_is_a_valid_canonical_source()
    {
        var command = Command(null,
        [
            NewParty("Primary", "Tenant", "primary@example.com",
                LeaseManagementPartyRole.PrimaryTenant, true, 1),
            NewParty("Resident", "Only", null,
                LeaseManagementPartyRole.Occupant, false, null),
        ]);

        var act = () => PrepareMoveInHandler.ValidateCommandShape(command);

        act.Should().NotThrow();
    }

    [Fact]
    public void Approved_application_with_existing_household_is_a_valid_canonical_source()
    {
        var command = Command(17,
        [
            new PrepareMoveInParty(31, null, LeaseManagementPartyRole.PrimaryTenant,
                false, "Approved applicant", true, 1, true),
        ]);

        var act = () => PrepareMoveInHandler.ValidateCommandShape(command);

        act.Should().NotThrow();
    }

    [Fact]
    public void Party_cannot_select_existing_and_new_tenant_at_the_same_time()
    {
        var command = Command(null,
        [
            new PrepareMoveInParty(31,
                new PrepareMoveInNewTenant("Duplicate", "Source", "duplicate@example.com", null, null),
                LeaseManagementPartyRole.PrimaryTenant, false, "Invalid dual source", true, 1, true),
        ]);

        var act = () => PrepareMoveInHandler.ValidateCommandShape(command);

        act.Should().Throw<ArgumentException>();
    }

    private static PrepareMoveInParty NewParty(
        string firstName,
        string lastName,
        string? email,
        LeaseManagementPartyRole role,
        bool signer,
        short? signingOrder) => new(
            null,
            new PrepareMoveInNewTenant(firstName, lastName, email, null, null),
            role,
            false,
            "Initial household",
            signer,
            signingOrder,
            signer);

    private static PrepareMoveInCommand Command(
        int? applicationId,
        IReadOnlyList<PrepareMoveInParty> parties) => new(
            1,
            applicationId,
            2,
            3,
            Guid.NewGuid(),
            4,
            1,
            null,
            new DateOnly(2026, 8, 1),
            parties,
            5,
            LeaseAgreementTermType.FixedTerm,
            new DateOnly(2026, 8, 1),
            new DateOnly(2027, 7, 31),
            1_200m,
            1,
            1_200m,
            50m,
            5,
            1,
            "{}",
            true,
            null,
            null,
            null,
            "prepare-move-in:test");
}
