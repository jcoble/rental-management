using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Payments;

public interface ITenantMoneyCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int TenantAccountId { get; }
    int ActorUserId { get; }
    Guid AuthSessionId { get; }
    int AccessContextId { get; }
    long ExpectedAccessRevision { get; }
    string RequiredCapability { get; }
    string DeliveryIdempotencyKey { get; }
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
    bool AllocateOldestCharges,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string RequiredCapability,
    string BusinessKey,
    string DeliveryIdempotencyKey,
    DateTime RecordedAtUtc = default) : ITenantMoneyCommand;

public sealed record RecordTenantReceiptResult(
    bool Found,
    int TenantAccountId,
    long LedgerEntryId,
    long PaymentAttemptId,
    decimal Amount,
    decimal AllocatedAmount,
    int AllocationCount) : IAtomicResultData;

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
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

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
    string? Error) : IAtomicResultData;

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
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

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
    string? Error) : IAtomicResultData;

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
    string? Error) : IAtomicResultData;

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
    string? Error) : IAtomicResultData;
