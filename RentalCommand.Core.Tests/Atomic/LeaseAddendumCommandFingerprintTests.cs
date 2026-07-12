using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Tests.Atomic;

public sealed class LeaseAddendumCommandFingerprintTests
{
    [Fact]
    public void Financial_effect_change_changes_addendum_draft_fingerprint()
    {
        var original = CreateDraft(25m);
        var changed = CreateDraft(35m);

        Fingerprint("lease-addendum.draft.create", original).Should().NotBe(
            Fingerprint("lease-addendum.draft.create", changed));
    }

    [Fact]
    public void Void_reason_change_changes_legal_command_fingerprint()
    {
        var original = VoidAddendum("MutualRescission");
        var changed = VoidAddendum("ExecutionError");

        Fingerprint("lease-addendum.void", original).Should().NotBe(
            Fingerprint("lease-addendum.void", changed));
    }

    [Fact]
    public void Account_close_reason_change_changes_command_fingerprint()
    {
        var original = CloseAccount("MoveOutSettled");
        var changed = CloseAccount("Transferred");

        Fingerprint("tenant-account.close", original).Should().NotBe(
            Fingerprint("tenant-account.close", changed));
    }

    private static string Fingerprint<T>(string type, T command) where T : IAtomicCommandData =>
        new AtomicCommandIdentity(type, "same-key").BindRequest(command).RequestFingerprint;

    private static CreateLeaseAddendumDraftCommand CreateDraft(decimal delta) => new(
        1, 2, 3, "PET-2-1", LeaseAddendumPurpose.Pet,
        new DateOnly(2026, 8, 1), null, 1, "{\"pet\":true}", 4, 1,
        [new(5, 6, LeaseLegalSignerRole.PrimaryTenant, "Tenant One", "tenant@example.com", 1, true)],
        [new(LeaseAddendumFinancialEffectType.RecurringRentDelta, delta, "USD", "PET_RENT",
            new DateOnly(2026, 8, 1), null, null, "Monthly pet rent")],
        7, Guid.Parse("11111111-1111-1111-1111-111111111111"), 8, 9,
        "addendum-create:1:2:key");

    private static VoidLeaseAddendumCommand VoidAddendum(string reason) => new(
        1, 2, 3, reason, null, 7,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), 8, 9,
        "addendum-void:1:3:key");

    private static CloseTenantAccountCommand CloseAccount(string reason) => new(
        1, 2, 10, reason, null, 7,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), 8, 9,
        "tenant-account-close:1:10:key");
}
