using RentalCommand.Core.Atomic;

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
    string DeliveryIdempotencyKey) : ITenantMoneyCommand;

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

public sealed record SecurityDepositMutationResult(
    bool Found,
    bool Applied,
    int TenantAccountId,
    int SecurityDepositAccountId,
    long SecurityDepositEntryId,
    long? TenantLedgerEntryId,
    decimal Amount,
    string? Error) : IAtomicResultData;
