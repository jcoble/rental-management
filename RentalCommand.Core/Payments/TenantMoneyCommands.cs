using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Payments;

public interface ITenantMoneyCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int TenantAccountId { get; }
    int ActorUserId { get; }
    [AtomicFingerprintIgnore] Guid AuthSessionId { get; }
    [AtomicFingerprintIgnore] int AccessContextId { get; }
    [AtomicFingerprintIgnore] long ExpectedAccessRevision { get; }
    string RequiredCapability { get; }
    [AtomicFingerprintIgnore] string DeliveryIdempotencyKey { get; }
}

public sealed record RecordTenantReceiptCommand(
    int PortfolioId,
    int TenantAccountId,
    decimal Amount,
    DateOnly EffectiveOn,
    string Description,
    string PaymentMethodSummary,
    string? ExternalReference,
    string? PayerName,
    string? CheckNumber,
    string? BankName,
    int? SourceStoredFileId,
    long? TargetChargeEntryId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore] DateTime RecordedAtUtc = default) : ITenantMoneyCommand
{
    public bool AllocateOldestCharges { get; init; }
}

public sealed record RecordTenantReceiptResult(
    bool Found,
    int TenantAccountId,
    long LedgerEntryId,
    long PaymentAttemptId,
    decimal Amount,
    decimal AllocatedAmount,
    int AllocationCount);

public sealed record PostTenantChargeCommand(
    int PortfolioId,
    int TenantAccountId,
    decimal Amount,
    DateOnly EffectiveOn,
    DateOnly DueOn,
    string Description,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey,
    int? IncomeLedgerAccountId = null,
    DateOnly? ServicePeriodStartOn = null,
    DateOnly? ServicePeriodEndOn = null) : ITenantMoneyCommand;

public sealed record ReverseTenantChargeCommand(
    int PortfolioId,
    int TenantAccountId,
    long ReversesEntryId,
    DateOnly EffectiveOn,
    string Reason,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public sealed record TenantChargeMutationResult(
    bool Found,
    bool Applied,
    int TenantAccountId,
    long LedgerEntryId,
    long? ReversesEntryId,
    decimal Amount,
    string? Error);

public sealed record PostTenantCreditCommand(
    int PortfolioId,
    int TenantAccountId,
    decimal Amount,
    DateOnly EffectiveOn,
    string Description,
    int? SourceStoredFileId,
    bool AllocateOldestCharges,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey,
    long? TargetChargeEntryId = null,
    int? IncomeLedgerAccountId = null) : ITenantMoneyCommand;

public sealed record PostTenantAdjustmentCommand(
    int PortfolioId,
    int TenantAccountId,
    TenantLedgerDirection Direction,
    decimal Amount,
    DateOnly EffectiveOn,
    string Description,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public sealed record ReverseTenantLedgerEntryCommand(
    int PortfolioId,
    int TenantAccountId,
    long ReversesEntryId,
    DateOnly EffectiveOn,
    string Reason,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public sealed record RefundTenantPaymentCommand(
    int PortfolioId,
    int TenantAccountId,
    long PaymentEntryId,
    DateOnly EffectiveOn,
    string Reason,
    string? PaymentMethodSummary,
    string? ExternalReference,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public enum TenantPaymentRefundOutcome
{
    Refunded,
    AlreadyRefunded,
    ExternalCorrectionUnavailable,
}

public sealed record TenantPaymentRefundResult(
    bool Found,
    bool Applied,
    TenantPaymentRefundOutcome Outcome,
    int TenantAccountId,
    long PaymentEntryId,
    long? RefundEntryId,
    long? ProviderPaymentAttemptId,
    decimal Amount,
    decimal CompensatedAllocationAmount,
    int CompensatedAllocationCount,
    string? Error);

public sealed record TenantLedgerMutationResult(
    bool Found,
    bool Applied,
    int TenantAccountId,
    long LedgerEntryId,
    long? ReversesEntryId,
    TenantLedgerEntryType EntryType,
    TenantLedgerDirection Direction,
    decimal Amount,
    decimal AllocatedAmount,
    int AllocationCount,
    string? Error);

public interface ISecurityDepositMoneyCommand : ITenantMoneyCommand
{
    int SecurityDepositAccountId { get; }
}

public sealed record FundSecurityDepositCommand(
    int PortfolioId,
    int TenantAccountId,
    int SecurityDepositAccountId,
    decimal Amount,
    DateOnly EffectiveOn,
    string Description,
    string PaymentMethodSummary,
    string? ExternalReference,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ISecurityDepositMoneyCommand;

public sealed record DeductSecurityDepositCommand(
    int PortfolioId,
    int TenantAccountId,
    int SecurityDepositAccountId,
    decimal Amount,
    DateOnly EffectiveOn,
    string Reason,
    string? Notes,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ISecurityDepositMoneyCommand;

public sealed record RefundSecurityDepositCommand(
    int PortfolioId,
    int TenantAccountId,
    int SecurityDepositAccountId,
    decimal? Amount,
    DateOnly EffectiveOn,
    string Description,
    string? ExternalReference,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ISecurityDepositMoneyCommand;

public sealed record ReverseSecurityDepositEntryCommand(
    int PortfolioId,
    int TenantAccountId,
    int SecurityDepositAccountId,
    long ReversesEntryId,
    DateOnly EffectiveOn,
    string Reason,
    int? SourceStoredFileId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ISecurityDepositMoneyCommand;

public sealed record SecurityDepositMutationResult(
    bool Found,
    bool Applied,
    int TenantAccountId,
    int SecurityDepositAccountId,
    long SecurityDepositEntryId,
    long? TenantLedgerEntryId,
    decimal Amount,
    string? Error);

/// <summary>
/// Reconstructs a portfolio's exact historical security-deposit opening position from its
/// fully-executed governing Agreements. This is an opening-position recovery, not a new tenant
/// payment: it creates deposit subledgers and immutable deposit receipt facts without inventing
/// tenant charges, payment attempts, or tenant-ledger receipts.
/// </summary>
public sealed record RecoverOpeningSecurityDepositsCommand(
    int PortfolioId,
    DateOnly EffectiveOn,
    int ExpectedAccountCount,
    decimal ExpectedTotal,
    string FinancialReference,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record RecoverOpeningSecurityDepositsResult(
    int AccountCount,
    int CreatedAccountCount,
    int CreatedEntryCount,
    decimal ReconciledTotal,
    string FinancialReference);

/// <summary>
/// Repairs one reviewed historical rent period by replacing an incorrect agreement-backed rent
/// charge and moving its existing receipt allocation to the corrected charge.
/// </summary>
public sealed record RecoverHistoricalRentChargeCommand(
    int PortfolioId,
    int TenantAccountId,
    int LeaseAgreementId,
    long ExistingRentChargeEntryId,
    long ExistingReceiptEntryId,
    long ExistingAllocationId,
    DateOnly ExpectedCurrentRentTrackingStartOn,
    DateOnly CorrectRentTrackingStartOn,
    DateOnly RentPeriodStartOn,
    DateOnly ExpectedExistingChargeDueOn,
    decimal ExpectedExistingChargeAmount,
    decimal ExpectedReceiptAmount,
    decimal CorrectRentAmount,
    string FinancialReference,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public sealed record RecoverHistoricalRentChargeResult(
    bool Applied,
    int TenantAccountId,
    int LeaseAgreementId,
    long ReversedRentChargeEntryId,
    long ReversalEntryId,
    long ReplacementRentChargeEntryId,
    long ReceiptEntryId,
    long ReversedAllocationId,
    long ReplacementAllocationId,
    DateOnly RentTrackingStartOn,
    decimal ReversedRentAmount,
    decimal ReplacementRentAmount,
    decimal ReallocatedAmount,
    string FinancialReference);

/// <summary>
/// Repairs one reviewed allocation that was appended after its payment receipt had already been
/// fully refunded. The original allocation remains immutable; recovery appends one exact negative
/// allocation tied to it.
/// </summary>
public sealed record RecoverRefundedTenantAllocationCommand(
    int PortfolioId,
    int TenantAccountId,
    long ExistingAllocationId,
    long ExpectedDebitEntryId,
    long ExpectedCreditEntryId,
    long ExpectedRefundPaymentAttemptId,
    decimal ExpectedAllocationAmount,
    decimal ExpectedRefundAmount,
    string FinancialReference,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

public sealed record RecoverRefundedTenantAllocationResult(
    bool Applied,
    int TenantAccountId,
    long ReversedAllocationId,
    long ReversalAllocationId,
    long DebitEntryId,
    long CreditEntryId,
    long RefundPaymentAttemptId,
    decimal ReversedAmount,
    string FinancialReference);

public sealed record RecoverLateFeeChargeRow(
    int TenantAccountId,
    long ExistingLateFeeEntryId,
    decimal ExpectedExistingAmount,
    decimal ReplacementAmount,
    bool AlreadyReversed) : IAtomicCommandData;

/// <summary>
/// Repairs a reviewed batch of incorrect late fees as one atomic recovery action. The row list is
/// exact input, not a selector: every row must match current ledger state before anything mutates.
/// </summary>
public sealed record RecoverLateFeeChargesCommand(
    int PortfolioId,
    IReadOnlyList<RecoverLateFeeChargeRow> Corrections,
    int ExpectedReviewedChargeCount,
    int ExpectedReversedChargeCount,
    int ExpectedReplacementChargeCount,
    decimal ExpectedReversedTotal,
    decimal ExpectedReplacementTotal,
    string FinancialReference,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record RecoverLateFeeChargesResult(
    int ReversedChargeCount,
    int ReplacementChargeCount,
    int ReversedAllocationCount,
    int ReplacementAllocationCount,
    decimal ReversedTotal,
    decimal ReplacementTotal,
    decimal ReversedAllocationTotal,
    decimal ReplacementAllocationTotal,
    string FinancialReference);
