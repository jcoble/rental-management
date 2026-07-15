using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class LeaseLegalSignerValidationTests
{
    [Fact]
    public void Agreement_draft_rejects_a_mixed_required_and_optional_signer_snapshot()
    {
        var command = new EditLeaseAgreementDraftCommand(
            1, 2, 3, 1, "A-1", LeaseAgreementTermType.FixedTerm,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1),
            1000m, 1, 1000m, 25m, 5, 1, "{}", 4,
            [
                AgreementSigner(10, 20, LeaseLegalSignerRole.PrimaryTenant, "tenant@example.com", true, 1),
                AgreementSigner(null, null, LeaseLegalSignerRole.Manager, "manager@example.com", false, 2),
            ],
            5, Guid.NewGuid(), 6, 1, "agreement-edit:test");

        var act = () => LeaseAgreementDraftCommandSupport.ValidateEditShape(command);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Addendum_draft_rejects_a_mixed_required_and_optional_signer_snapshot()
    {
        var command = new CreateLeaseAddendumDraftCommand(
            1, 2, 3, "ADD-1", LeaseAddendumPurpose.Other,
            new DateOnly(2026, 2, 1), null, 1, "{}", 4,
            [
                AddendumSigner(10, 20, LeaseLegalSignerRole.PrimaryTenant, "tenant@example.com", true, 1),
                AddendumSigner(null, null, LeaseLegalSignerRole.Manager, "manager@example.com", false, 2),
            ],
            [], 5, Guid.NewGuid(), 6, 1, "addendum-create:test");

        var act = () => LeaseAddendumCommandSupport.Validate(command);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Correction_successor_requires_a_durable_reason()
    {
        var command = Successor(LeaseAgreementChangeType.Correction, "   ");

        var act = () => LeaseAgreementDraftCommandSupport.ValidateSuccessorShape(command);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Correction_successor_accepts_a_durable_reason()
    {
        var command = Successor(LeaseAgreementChangeType.Correction, "Correct misspelled resident name.");

        var act = () => LeaseAgreementDraftCommandSupport.ValidateSuccessorShape(command);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(LeaseAgreementChangeType.Renewal)]
    [InlineData(LeaseAgreementChangeType.MonthToMonth)]
    [InlineData(LeaseAgreementChangeType.Restatement)]
    public void Non_correction_successor_rejects_a_correction_reason(LeaseAgreementChangeType changeType)
    {
        var command = Successor(changeType, "This reason does not belong on this successor type.");

        var act = () => LeaseAgreementDraftCommandSupport.ValidateSuccessorShape(command);

        act.Should().Throw<ArgumentException>();
    }

    private static LeaseAgreementDraftSignerInput AgreementSigner(
        int? partyId, int? tenantId, LeaseLegalSignerRole role, string email, bool required, short order) =>
        new(partyId, tenantId, role, role.ToString(), email, order, required);

    private static LeaseAddendumDraftSignerInput AddendumSigner(
        int? partyId, int? tenantId, LeaseLegalSignerRole role, string email, bool required, short order) =>
        new(partyId, tenantId, role, role.ToString(), email, order, required);

    private static CreateLeaseAgreementSuccessorDraftCommand Successor(
        LeaseAgreementChangeType changeType,
        string? correctionReason) => new(
        1,
        2,
        3,
        changeType,
        new DateOnly(2027, 1, 1),
        changeType == LeaseAgreementChangeType.MonthToMonth ? null : new DateOnly(2027, 12, 31),
        new DateOnly(2027, 1, 1),
        correctionReason,
        null,
        [],
        5,
        Guid.NewGuid(),
        6,
        1,
        "successor-create:test");
}
