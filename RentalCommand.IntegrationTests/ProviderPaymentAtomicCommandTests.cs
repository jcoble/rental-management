using FluentAssertions;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Compile-time contract guards for the provider cutover. PostgreSQL transaction, receipt,
/// allocation, replay, and inbox-claim verification is run by the serialized foundation suite.
/// </summary>
public sealed class ProviderPaymentAtomicCommandTests
{
    [Fact]
    public void ProviderCreateContracts_IdentifyCanonicalAccountChargeAndAttempt()
    {
        var prepare = new PrepareProviderPaymentCreateCommand(
            PortfolioId: 4,
            TenantAccountId: 12,
            ChargeLedgerEntryId: 90,
            ActorUserId: 7,
            TenantId: 8,
            AutopayEnrollmentId: null,
            Provider: "stripe",
            IdempotencyKey: "checkout:tenant-charge:90",
            Currency: "USD",
            PreparedAtUtc: DateTime.UnixEpoch);
        var finalize = new FinalizeProviderPaymentCreateCommand(
            prepare.PortfolioId,
            prepare.TenantAccountId,
            PaymentAttemptId: 100,
            prepare.Provider,
            prepare.IdempotencyKey,
            ProviderPaymentId: "cs_123",
            TenantPaymentAttemptState.Submitted,
            FailureReason: null,
            RecordedAtUtc: DateTime.UnixEpoch);

        prepare.TenantAccountId.Should().Be(12);
        prepare.ChargeLedgerEntryId.Should().Be(90);
        finalize.PaymentAttemptId.Should().Be(100);
        typeof(PrepareProviderPaymentCreateCommand).GetProperty("PaymentId").Should().BeNull();
        typeof(FinalizeProviderPaymentCreateCommand).GetProperty("PaymentTransactionId").Should().BeNull();
    }

    [Fact]
    public void VerifiedSetupContract_CarriesOnlyCanonicalEnrollmentReferences()
    {
        var command = new RecordVerifiedProviderPaymentEventCommand(
            "stripe", "evt_1", "checkout.session.completed", "{}", "cs_1",
            ProviderPaymentEventKind.SetupCompleted, null, null, null,
            DateTime.UnixEpoch, DateTime.UnixEpoch,
            EnrollmentPortfolioId: 4,
            EnrollmentTenantAccountId: 12,
            EnrollmentAuthorizingPartyId: 18,
            EnrollmentActorUserId: 7,
            EnrollmentPaymentAttemptId: 100,
            ProviderCustomerId: "cus_1",
            ProviderPaymentMethodId: "pm_1");

        command.EnrollmentTenantAccountId.Should().Be(12);
        command.EnrollmentAuthorizingPartyId.Should().Be(18);
        typeof(RecordVerifiedProviderPaymentEventCommand).GetProperty("EnrollmentLeaseId").Should().BeNull();
    }
}
