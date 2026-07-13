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

    private static LeaseAgreementDraftSignerInput AgreementSigner(
        int? partyId, int? tenantId, LeaseLegalSignerRole role, string email, bool required, short order) =>
        new(partyId, tenantId, role, role.ToString(), email, order, required);

    private static LeaseAddendumDraftSignerInput AddendumSigner(
        int? partyId, int? tenantId, LeaseLegalSignerRole role, string email, bool required, short order) =>
        new(partyId, tenantId, role, role.ToString(), email, order, required);
}
