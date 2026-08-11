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
    [property: AtomicFingerprintIgnore] DateTime PreparedAtUtc) : IAtomicCommandData;

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
    string? ProviderPaymentMethodId,
    TenantPaymentAttemptState State = TenantPaymentAttemptState.Prepared,
    Guid? ProviderFenceToken = null,
    string? ProviderPaymentId = null,
    [property: AtomicFingerprintIgnore] DateTime? PreparedAtUtc = null);

/// <summary>Claims the durable charge reservation immediately before a provider create.</summary>
public sealed record SubmitProviderPaymentCreateCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    [property: AtomicFingerprintIgnore] DateTime SubmittedAtUtc) : IAtomicCommandData;

public enum SubmitProviderPaymentCreateOutcome { Submitted, AlreadySubmitted, Succeeded, Failed, Canceled, NotFound }

public sealed record SubmitProviderPaymentCreateResult(
    SubmitProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    TenantPaymentAttemptState State,
    decimal Amount,
    string Currency,
    string Provider,
    string IdempotencyKey,
    Guid? ProviderFenceToken,
    string? ProviderPaymentId = null,
    [property: AtomicFingerprintIgnore] DateTime? PreparedAtUtc = null);

/// <summary>Creates a durable verification attempt before opening provider setup.</summary>
public sealed record PrepareProviderAutopaySetupCommand(
    int PortfolioId,
    int TenantAccountId,
    int TenantId,
    int ActorUserId,
    string Provider,
    string IdempotencyKey,
    string Currency,
    [property: AtomicFingerprintIgnore] DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PrepareProviderAutopaySetupOutcome { Prepared, NotFound }

public sealed record PrepareProviderAutopaySetupResult(
    PrepareProviderAutopaySetupOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    int AuthorizingPartyId,
    int ActorUserId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    TenantPaymentAttemptState State = TenantPaymentAttemptState.Prepared,
    Guid? ProviderFenceToken = null,
    string? ProviderPaymentId = null);

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
    [property: AtomicFingerprintIgnore] DateTime RecordedAtUtc,
    Guid? ProviderFenceToken = null) : IAtomicCommandData;

public enum FinalizeProviderPaymentCreateOutcome { Applied, NotFound, AlreadyFinalized }

public sealed record FinalizeProviderPaymentCreateResult(
    FinalizeProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string ProviderPaymentId,
    TenantPaymentAttemptState State);

/// <summary>Records a provider-create failure without inventing a provider receipt.</summary>
public sealed record FailProviderPaymentCreateCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    string? FailureCode,
    string FailureReason,
    [property: AtomicFingerprintIgnore] DateTime RecordedAtUtc,
    Guid? ProviderFenceToken = null) : IAtomicCommandData;

public sealed record FailProviderPaymentCreateResult(
    bool Found,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    TenantPaymentAttemptState State);

/// <summary>Explicitly abandons an unresolved prepared attempt after provider reconciliation.</summary>
public sealed record AbandonProviderPaymentAttemptCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    string Reason,
    [property: AtomicFingerprintIgnore] DateTime AbandonedAtUtc) : IAtomicCommandData;

public enum AbandonProviderPaymentAttemptOutcome { Applied, AlreadyTerminal, NotFound, ReconciliationRequired }

public sealed record AbandonProviderPaymentAttemptResult(
    AbandonProviderPaymentAttemptOutcome Outcome,
    int PortfolioId,
    int TenantAccountId,
    long PaymentAttemptId,
    TenantPaymentAttemptState State);

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
    [property: AtomicFingerprintIgnore] DateTime ReceivedAtUtc,
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
    TenantPaymentAttemptState? AttemptState);

public sealed record ReconcileClaimedProviderPaymentEventCommand(
    long ProviderInboxEventId,
    string ClaimOwner,
    Guid ClaimToken,
    [property: AtomicFingerprintIgnore] DateTime ReconciledAtUtc) : IAtomicCommandData;

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
    DateTime? NextAttemptAtUtc);
