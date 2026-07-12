using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Tests.Atomic;

public sealed class LeaseAgreementDraftFingerprintTests
{
    [Fact]
    public void Edit_payload_change_changes_kernel_fingerprint()
    {
        var original = Command(baseRent: 1250m);
        var changed = Command(baseRent: 1300m);

        new AtomicCommandIdentity("lease-agreement.draft.edit", "same-key")
            .BindRequest(original).RequestFingerprint.Should().NotBe(
                new AtomicCommandIdentity("lease-agreement.draft.edit", "same-key")
                    .BindRequest(changed).RequestFingerprint);
    }

    private static EditLeaseAgreementDraftCommand Command(decimal baseRent) => new(
        1, 2, 3, 4, "AGR-2-V1", LeaseAgreementTermType.FixedTerm,
        new DateOnly(2026, 8, 1), new DateOnly(2027, 7, 31), new DateOnly(2026, 8, 1),
        baseRent, 1, 1250m, 50m, 5, 1, "{\"rent\":true}", 6, 1,
        [new(9, 10, LeaseLegalSignerRole.PrimaryTenant, "Tenant One", "tenant@example.com", 1, true)],
        11, Guid.Parse("11111111-1111-1111-1111-111111111111"), 12, 13,
        "agreement-draft-edit:1:2:3:key");
}
