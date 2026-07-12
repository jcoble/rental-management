using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Payments;

/// <summary>
/// Creates the durable local payment attempt before any provider call. A tenant id is supplied for
/// tenant-initiated checkout and is verified through the payment's lease in the handler.
/// </summary>
public sealed record PrepareProviderPaymentCreateCommand(
    int PortfolioId,
    int PaymentId,
    int? TenantId,
    string Provider,
    string IdempotencyKey,
    string Currency,
    DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PrepareProviderPaymentCreateOutcome
{
    Prepared,
    NotFound,
}

public sealed record PrepareProviderPaymentCreateResult(
    PrepareProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int PaymentId,
    int PaymentTransactionId,
    decimal Amount,
    string Currency,
    string Provider,
    string IdempotencyKey) : IAtomicResultData;

/// <summary>
/// Records the provider receipt returned after a payment-create request was made outside the
/// database transaction. The local transaction attempt and its idempotency key already exist.
/// </summary>
public sealed record FinalizeProviderPaymentCreateCommand(
    int PortfolioId,
    int PaymentId,
    int PaymentTransactionId,
    string Provider,
    string IdempotencyKey,
    string ProviderPaymentId,
    PaymentTransactionStatus Status,
    string? FailureReason,
    DateTime RecordedAtUtc) : IAtomicCommandData;

public enum FinalizeProviderPaymentCreateOutcome
{
    Applied,
    NotFound,
    AlreadyFinalized,
}

/// <summary>Receipt-safe result of binding a provider create receipt to its local attempt.</summary>
public sealed record FinalizeProviderPaymentCreateResult(
    FinalizeProviderPaymentCreateOutcome Outcome,
    int PortfolioId,
    int PaymentId,
    int PaymentTransactionId,
    string Provider,
    string ProviderPaymentId,
    PaymentTransactionStatus Status) : IAtomicResultData;

/// <summary>Normalized business meaning of a verified provider payment event.</summary>
public enum ProviderPaymentEventKind
{
    Pending,
    Succeeded,
    Failed,
    Canceled,
    SetupCompleted,
    Ignored,
}

/// <summary>
/// Persists and reconciles one signature-verified provider payment event. Signature verification
/// and provider retrieval happen before this command; handlers receive no SDK or remote dependency.
/// </summary>
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
    int? EnrollmentLeaseId = null,
    int? EnrollmentTenantId = null,
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null) : IAtomicCommandData;

public enum RecordProviderPaymentEventOutcome
{
    Applied,
    Duplicate,
    Unmatched,
}

/// <summary>Receipt-safe reconciliation result for a verified provider event.</summary>
public sealed record RecordVerifiedProviderPaymentEventResult(
    RecordProviderPaymentEventOutcome Outcome,
    long ProviderInboxEventId,
    int? PortfolioId,
    int? PaymentId,
    int? PaymentTransactionId,
    PaymentTransactionStatus? TransactionStatus) : IAtomicResultData;

/// <summary>
/// Reconciles one leased provider inbox row. Id, owner, and claim token fence every completion,
/// retry, and dead-letter transition against an expired worker claim.
/// </summary>
public sealed record ReconcileClaimedProviderPaymentEventCommand(
    long ProviderInboxEventId,
    string ClaimOwner,
    Guid ClaimToken,
    DateTime ReconciledAtUtc) : IAtomicCommandData;

public enum ReconcileProviderPaymentEventOutcome
{
    Applied,
    RetryScheduled,
    DeadLettered,
}

public sealed record ReconcileClaimedProviderPaymentEventResult(
    ReconcileProviderPaymentEventOutcome Outcome,
    long ProviderInboxEventId,
    int? PortfolioId,
    int? PaymentId,
    int? PaymentTransactionId,
    PaymentTransactionStatus? TransactionStatus,
    DateTime? NextAttemptAtUtc) : IAtomicResultData;
