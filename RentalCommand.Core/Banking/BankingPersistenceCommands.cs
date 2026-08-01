using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Banking;

public sealed record ApplyPlaidConnectionCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    [property: AtomicFingerprintIgnore] DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyPlaidConnectionResult(
    int ConnectionId,
    bool Created);

public sealed record PreparePlaidTokenExchangeCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    string ClientOperationId,
    string RequestHash,
    string PublicTokenHash,
    string InstitutionName,
    string AccountName,
    string? AccountMask,
    string? AccountType,
    string? AccountSubtype,
    [property: AtomicFingerprintIgnore] string ExternalAccountIdCipherText,
    string ExternalAccountIdHash,
    [property: AtomicFingerprintIgnore] DateTime PreparedAtUtc) : IAtomicCommandData;

public enum PreparePlaidTokenExchangeOutcome
{
    Prepared,
    Existing,
    Conflict,
}

public sealed record PreparePlaidTokenExchangeResult(
    PreparePlaidTokenExchangeOutcome Outcome,
    Guid ExchangeAttemptId);

public sealed record AdmitPlaidTokenExchangeCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    [property: AtomicFingerprintIgnore] DateTime AdmittedAtUtc) : IAtomicCommandData;

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
    Guid ExchangeAttemptId);

public sealed record RecordPlaidTokenExchangeReceiptCommand(
    int PortfolioId,
    Guid ExchangeAttemptId,
    string ProviderRequestIdentity,
    string ExternalItemIdCipherText,
    string ExternalItemIdHash,
    string ExternalAccessTokenCipherText,
    [property: AtomicFingerprintIgnore] DateTime RecordedAtUtc) : IAtomicCommandData;

public enum RecordPlaidTokenExchangeReceiptOutcome
{
    Recorded,
    NotFound,
    NotAdmitted,
    AlreadyRecorded,
}

public sealed record RecordPlaidTokenExchangeReceiptResult(
    RecordPlaidTokenExchangeReceiptOutcome Outcome,
    Guid ExchangeAttemptId);

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

public sealed record BankStatementInput(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal StatementMovement,
    string IsoCurrencyCode) : IAtomicCommandData;

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
    [property: AtomicFingerprintIgnore] DateTime AppliedAtUtc) : IAtomicCommandData;

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
    IReadOnlyList<int> AffectedTransactionIds);

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
    [property: AtomicFingerprintIgnore] DateTime ImportedAtUtc,
    BankStatementInput? Statement = null) : IAtomicCommandData;

public sealed record ImportBankTransactionsResult(
    int ConnectionId,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<int> ImportedTransactionIds,
    int? StatementId = null,
    DateOnly? StatementPeriodStart = null,
    DateOnly? StatementPeriodEnd = null,
    decimal? StatementOpeningBalance = null,
    decimal? StatementClosingBalance = null,
    decimal? StatementMovement = null,
    string? StatementIsoCurrencyCode = null);

public enum BankReconciliationAction
{
    MatchReceipt,
    MatchExpense,
    MatchLoanPayment,
    MatchOwnerDistribution,
    MatchTransfer,
    Clear,
    Dismiss,
    Ignore,
}

public sealed record ReconcileBankTransactionCommand(
    int PortfolioId,
    int TransactionId,
    BankReconciliationAction Action,
    int? TenantAccountId,
    long? TenantLedgerEntryId,
    int? ExpenseId,
    int? LoanPaymentId,
    int? OwnerDistributionId,
    int? TransferBankTransactionId,
    DateTime? ExpectedTransferUpdatedAtUtc,
    DateTime ExpectedUpdatedAtUtc,
    [property: AtomicFingerprintIgnore] DateTime AppliedAtUtc,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string RequiredCapability,
    string OperationKey,
    [property: AtomicFingerprintIgnore] DateTime? ResolvedSuggestionTransferUpdatedAtUtc = null)
    : IAtomicCommandData;

public enum ReconcileBankTransactionOutcome
{
    Applied,
    AlreadyApplied,
    TransactionNotFound,
    TargetNotFound,
    RouteRequired,
    StaleVersion,
}

public sealed record ReconciledBankTransactionSnapshot(
    int Id,
    int? PropertyId,
    string? PropertyName,
    int BankConnectionId,
    string InstitutionName,
    string AccountName,
    string ProviderTransactionId,
    DateTime PostedAt,
    DateTime? AuthorizedAt,
    string Description,
    string? MerchantName,
    decimal Amount,
    string IsoCurrencyCode,
    string? Category,
    int? MatchedTenantAccountId,
    long? MatchedTenantLedgerEntryId,
    int? MatchedExpenseId,
    int? MatchedLoanPaymentId,
    int? MatchedOwnerDistributionId,
    int? MatchedBankTransactionId,
    string MatchStatus,
    decimal? MatchConfidence,
    string? Notes,
    DateTime UpdatedAt);

public sealed record ReconcileBankTransactionResult(
    ReconcileBankTransactionOutcome Outcome,
    int TransactionId,
    ReconciledBankTransactionSnapshot? Transaction = null);

public sealed record RouteBankTransactionCommand(
    int PortfolioId,
    int TransactionId,
    int? PropertyId,
    DateTime ExpectedUpdatedAtUtc,
    [property: AtomicFingerprintIgnore] DateTime AppliedAtUtc,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string OperationKey) : IAtomicCommandData;

public enum RouteBankTransactionOutcome
{
    Applied,
    AlreadyApplied,
    TransactionNotFound,
    PropertyNotFound,
    StaleVersion,
}

public sealed record RouteBankTransactionResult(
    RouteBankTransactionOutcome Outcome,
    int TransactionId);
