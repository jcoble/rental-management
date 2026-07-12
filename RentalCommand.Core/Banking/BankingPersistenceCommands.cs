using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Banking;

public sealed record ApplyPlaidConnectionCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyPlaidConnectionResult(
    int ConnectionId,
    bool Created) : IAtomicResultData;

public sealed record PreparePlaidTokenExchangeCommand(
    int PortfolioId,
    string ClientOperationId,
    string RequestHash,
    string PublicTokenHash,
    string InstitutionName,
    string AccountName,
    string? AccountMask,
    string? AccountType,
    string? AccountSubtype,
    string ExternalAccountIdCipherText,
    string ExternalAccountIdHash,
    DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PreparePlaidTokenExchangeOutcome
{
    Prepared,
    Existing,
    Conflict,
}

public sealed record PreparePlaidTokenExchangeResult(
    PreparePlaidTokenExchangeOutcome Outcome,
    Guid ExchangeAttemptId) : IAtomicResultData;

public sealed record AdmitPlaidTokenExchangeCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    DateTime AdmittedAtUtc) : IAtomicCommandData;

public enum AdmitPlaidTokenExchangeOutcome
{
    Admitted,
    NotFound,
    AlreadyAdmitted,
    ReceiptRecorded,
    Completed,
}

public sealed record AdmitPlaidTokenExchangeResult(
    AdmitPlaidTokenExchangeOutcome Outcome,
    Guid ExchangeAttemptId) : IAtomicResultData;

public sealed record RecordPlaidTokenExchangeReceiptCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    string ProviderRequestIdentity,
    string ExternalItemIdCipherText,
    string ExternalItemIdHash,
    string ExternalAccessTokenCipherText,
    DateTime RecordedAtUtc) : IAtomicCommandData;

public enum RecordPlaidTokenExchangeReceiptOutcome
{
    Recorded,
    NotFound,
    NotAdmitted,
    AlreadyRecorded,
}

public sealed record RecordPlaidTokenExchangeReceiptResult(
    RecordPlaidTokenExchangeReceiptOutcome Outcome,
    Guid ExchangeAttemptId) : IAtomicResultData;

public sealed record BankTransactionInput(
    string ProviderTransactionId,
    DateTime PostedAtUtc,
    DateTime? AuthorizedAtUtc,
    string Description,
    string? MerchantName,
    decimal Amount,
    string IsoCurrencyCode,
    string? Category,
    string? RawData) : IAtomicCommandData;

/// <summary>
/// Narrow kernel-owned set merge used by banking atomic handlers. The implementation owns the
/// PostgreSQL statement; handlers cannot obtain a DbContext, connection, or raw-SQL capability.
/// </summary>
public interface IAtomicBankingPersistence
{
    Task<AtomicBankTransactionMergeResult> ApplyPlaidSyncAsync(
        int portfolioId,
        int connectionId,
        IReadOnlyList<BankTransactionInput> added,
        int addedInputCount,
        IReadOnlyList<BankTransactionInput> modified,
        int modifiedInputCount,
        IReadOnlyList<string> removedProviderTransactionIds,
        DateTime appliedAtUtc,
        CancellationToken ct = default);

    Task<AtomicBankTransactionMergeResult> ImportAsync(
        int portfolioId,
        int connectionId,
        IReadOnlyList<BankTransactionInput> transactions,
        int inputCount,
        DateTime importedAtUtc,
        CancellationToken ct = default);
}

public sealed record AtomicBankTransactionMutation(
    int TransactionId,
    string Operation,
    string? OldValues,
    string NewValues,
    string Reason);

public sealed record AtomicBankTransactionMergeResult(
    int ImportedCount,
    int ModifiedCount,
    int RemovedCount,
    int ChangedEventCount,
    int SkippedCount,
    IReadOnlyList<int> AffectedTransactionIds,
    IReadOnlyList<AtomicBankTransactionMutation> Mutations);

public sealed record ApplyPlaidSyncCommand(
    int PortfolioId,
    int ConnectionId,
    string? ExpectedCursorCipherText,
    string? NextCursorCipherText,
    IReadOnlyList<BankTransactionInput> Added,
    int AddedInputCount,
    IReadOnlyList<BankTransactionInput> Modified,
    int ModifiedInputCount,
    IReadOnlyList<string> RemovedProviderTransactionIds,
    string ProviderRequestIdentity,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public enum ApplyPlaidSyncOutcome
{
    Applied,
    ConnectionNotFound,
    StaleCursor,
}

public sealed record ApplyPlaidSyncResult(
    ApplyPlaidSyncOutcome Outcome,
    int ConnectionId,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<int> AffectedTransactionIds) : IAtomicResultData;

public sealed record ImportBankTransactionsCommand(
    int PortfolioId,
    string Provider,
    string InstitutionName,
    string AccountName,
    string? AccountMask,
    string? AccountType,
    string? AccountSubtype,
    IReadOnlyList<BankTransactionInput> Transactions,
    int InputCount,
    string RequestIdentity,
    DateTime ImportedAtUtc) : IAtomicCommandData;

public sealed record ImportBankTransactionsResult(
    int ConnectionId,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<int> ImportedTransactionIds) : IAtomicResultData;

public enum BankReconciliationAction
{
    MatchPayment,
    MatchExpense,
    Clear,
    Dismiss,
    Ignore,
}

public sealed record ReconcileBankTransactionCommand(
    int PortfolioId,
    int TransactionId,
    BankReconciliationAction Action,
    int? TargetEntityId,
    DateTime ExpectedUpdatedAtUtc,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public enum ReconcileBankTransactionOutcome
{
    Applied,
    TransactionNotFound,
    TargetNotFound,
    StaleVersion,
}

public sealed record ReconcileBankTransactionResult(
    ReconcileBankTransactionOutcome Outcome,
    int TransactionId) : IAtomicResultData;
