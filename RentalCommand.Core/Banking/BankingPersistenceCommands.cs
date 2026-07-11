using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Banking;

public sealed record ApplyPlaidConnectionCommand(
    int PortfolioId,
    string InstitutionName,
    string AccountName,
    string? AccountMask,
    string? AccountType,
    string? AccountSubtype,
    string ExternalItemIdCipherText,
    string ExternalAccountIdCipherText,
    string ExternalItemIdHash,
    string ExternalAccountIdHash,
    string ExternalAccessTokenCipherText,
    string ProviderRequestIdentity,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyPlaidConnectionResult(
    int ConnectionId,
    bool Created) : IAtomicResultData;

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

public sealed record ApplyPlaidSyncCommand(
    int PortfolioId,
    int ConnectionId,
    string? ExpectedCursorCipherText,
    string? NextCursorCipherText,
    IReadOnlyList<BankTransactionInput> Added,
    IReadOnlyList<BankTransactionInput> Modified,
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
