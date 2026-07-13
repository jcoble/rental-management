using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Payments;

/// <summary>Creates the canonical provider attempt before any remote money movement.</summary>
public sealed record PrepareProviderPaymentCreateCommand(
    int PortfolioId,
    int TenantAccountId,
    long ChargeLedgerEntryId,
    int ActorUserId,
    int? TenantId,
    int? AutopayEnrollmentId,
    string Provider,
    string IdempotencyKey,
    string Currency,
    DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PrepareProviderPaymentCreateOutcome { Prepared, NotFound }

public sealed record PrepareProviderPaymentCreateResult(
    PrepareProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    long ChargeLedgerEntryId,
    long PaymentAttemptId,
    decimal Amount,
    string Currency,
    string Provider,
    string IdempotencyKey,
    string? ProviderCustomerId,
    string? ProviderPaymentMethodId) : IAtomicResultData;

/// <summary>Creates a durable verification attempt before opening provider setup.</summary>
public sealed record PrepareProviderAutopaySetupCommand(
    int PortfolioId,
    int TenantAccountId,
    int TenantId,
    int ActorUserId,
    string Provider,
    string IdempotencyKey,
    string Currency,
    DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PrepareProviderAutopaySetupOutcome { Prepared, NotFound }

public sealed record PrepareProviderAutopaySetupResult(
    PrepareProviderAutopaySetupOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    int AuthorizingPartyId,
    int ActorUserId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey) : IAtomicResultData;

/// <summary>Durably binds the provider receipt returned outside the database transaction.</summary>
public sealed record FinalizeProviderPaymentCreateCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    string ProviderPaymentId,
    TenantPaymentAttemptState State,
    string? FailureReason,
    DateTime RecordedAtUtc) : IAtomicCommandData;

public enum FinalizeProviderPaymentCreateOutcome { Applied, NotFound, AlreadyFinalized }

public sealed record FinalizeProviderPaymentCreateResult(
    FinalizeProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string ProviderPaymentId,
    TenantPaymentAttemptState State) : IAtomicResultData;

/// <summary>Records a provider-create failure without inventing a provider receipt.</summary>
public sealed record FailProviderPaymentCreateCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    string? FailureCode,
    string FailureReason,
    DateTime RecordedAtUtc) : IAtomicCommandData;

public sealed record FailProviderPaymentCreateResult(
    bool Found,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    TenantPaymentAttemptState State) : IAtomicResultData;

public enum ProviderPaymentEventKind
{
    Pending,
    Succeeded,
    Failed,
    Canceled,
    SetupCompleted,
    Ignored,
}

/// <summary>Provider-neutral facts from one signature-verified event.</summary>
public sealed record RecordVerifiedProviderPaymentEventCommand(
    string Provider,
    string ProviderEventId,
    string ProviderEventType,
    string PayloadJson,
    string ProviderPaymentId,
    ProviderPaymentEventKind EventKind,
    decimal? Amount,
    string? Currency,
    string? FailureReason,
    DateTime? OccurredAtUtc,
    DateTime ReceivedAtUtc,
    int? EnrollmentPortfolioId = null,
    int? EnrollmentTenantAccountId = null,
    int? EnrollmentAuthorizingPartyId = null,
    int? EnrollmentActorUserId = null,
    long? EnrollmentPaymentAttemptId = null,
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null) : IAtomicCommandData;

public enum RecordProviderPaymentEventOutcome
{
    Applied,
    AlreadyInState,
    Duplicate,
    Unmatched,
    Conflict,
}

public sealed record RecordVerifiedProviderPaymentEventResult(
    RecordProviderPaymentEventOutcome Outcome,
    long ProviderInboxEventId,
    int? PortfolioId,
    int? TenantAccountId,
    long? PaymentAttemptId,
    TenantPaymentAttemptState? AttemptState) : IAtomicResultData;

public sealed record ReconcileClaimedProviderPaymentEventCommand(
    long ProviderInboxEventId,
    string ClaimOwner,
    Guid ClaimToken,
    DateTime ReconciledAtUtc) : IAtomicCommandData;

public enum ReconcileProviderPaymentEventOutcome
{
    Applied,
    AlreadyInState,
    RetryScheduled,
    DeadLettered,
    Conflict,
}

public sealed record ReconcileClaimedProviderPaymentEventResult(
    ReconcileProviderPaymentEventOutcome Outcome,
    long ProviderInboxEventId,
    int? PortfolioId,
    int? TenantAccountId,
    long? PaymentAttemptId,
    TenantPaymentAttemptState? AttemptState,
    DateTime? NextAttemptAtUtc) : IAtomicResultData;
