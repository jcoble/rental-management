using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Core.Tests.Atomic;

public sealed class LeaseEndingDispositionCommandFingerprintTests
{
    [Fact]
    public void Disposition_dates_and_reason_are_bound_to_the_atomic_request_fingerprint()
    {
        var original = Command(
            LeaseManagementEndingDisposition.NonRenewalMoveOut,
            "Tenant gave notice.");
        var changedReason = Command(
            LeaseManagementEndingDisposition.NonRenewalMoveOut,
            "Landlord chose not to renew.");
        var changedDisposition = Command(
            LeaseManagementEndingDisposition.OfferMonthToMonth,
            "Continue without a fixed term.",
            includeMoveOutDates: false);

        var identity = new AtomicCommandIdentity(
            "lease-management.ending-disposition",
            "same-key");
        var originalFingerprint = identity.BindRequest(original).RequestFingerprint;

        originalFingerprint.Should().NotBe(
            identity.BindRequest(changedReason).RequestFingerprint);
        originalFingerprint.Should().NotBe(
            identity.BindRequest(changedDisposition).RequestFingerprint);
    }

    private static RecordLeaseEndingDispositionCommand Command(
        LeaseManagementEndingDisposition disposition,
        string reason,
        bool includeMoveOutDates = true) =>
        new(
            1,
            2,
            3,
            disposition,
            includeMoveOutDates ? new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc) : null,
            includeMoveOutDates ? new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc) : null,
            reason,
            4,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            5,
            6,
            "lease-ending:1:2:key");
}
