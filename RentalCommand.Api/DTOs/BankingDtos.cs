namespace RentalCommand.Api.DTOs;

public class BankingSummaryResponse
{
    public int ConnectionCount { get; set; }
    public int TransactionCount { get; set; }
    public int UnmatchedCount { get; set; }
    public int SuggestedMatchCount { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public IReadOnlyList<BankConnectionResponse> Connections { get; set; } = [];
    public IReadOnlyList<BankTransactionResponse> RecentTransactions { get; set; } = [];
}

public class PlaidSettingsResponse
{
    public string PlaidEnvironment { get; set; } = "sandbox";
    public bool Configured { get; set; }
}

public class PlaidLinkTokenResponse
{
    public string LinkToken { get; set; } = string.Empty;
    public DateTime? Expiration { get; set; }
    public string? RequestId { get; set; }
    public bool Configured { get; set; }
    public string? Message { get; set; }
}

public class PlaidLinkTokenRequest
{
    public string? Platform { get; set; }
}

public class ExchangePlaidPublicTokenRequest
{
    public string ClientOperationId { get; set; } = string.Empty;
    public string PublicToken { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? AccountMask { get; set; }
    public string? AccountType { get; set; }
    public string? AccountSubtype { get; set; }
}

public class BankConnectionResponse
{
    public int Id { get; set; }
    public string Provider { get; set; } = "Plaid";
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? AccountMask { get; set; }
    public string? AccountType { get; set; }
    public string? AccountSubtype { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime? LastSyncedAt { get; set; }
}

public class BankTransactionResponse
{
    public int Id { get; set; }
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int BankConnectionId { get; set; }
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ProviderTransactionId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? MerchantName { get; set; }
    public decimal Amount { get; set; }
    public string IsoCurrencyCode { get; set; } = "USD";
    public string? Category { get; set; }
    public int? MatchedTenantAccountId { get; set; }
    public long? MatchedTenantLedgerEntryId { get; set; }
    public int? MatchedExpenseId { get; set; }
    public string MatchStatus { get; set; } = "Unmatched";
    public decimal? MatchConfidence { get; set; }
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
    public BankMatchSuggestionResponse? SuggestedMatch { get; set; }
}

public class BankTransactionListResponse
{
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public IReadOnlyList<BankTransactionResponse> Items { get; set; } = [];
}

public class BankMatchSuggestionResponse
{
    public string EntityType { get; set; } = string.Empty;
    public long EntityId { get; set; }
    public int? TenantAccountId { get; set; }
    public decimal Confidence { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Purpose-built bank-line projection for property-scoped reconciliation. It deliberately omits
/// bank connection ids, institution/account details, provider ids, internal match target ids, and
/// notes. Those fields belong to workspace bank administration, not operational reconciliation.
/// </summary>
public class OperationalBankTransactionResponse
{
    public int Id { get; set; }
    public DateTime PostedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? MerchantName { get; set; }
    public decimal Amount { get; set; }
    public string IsoCurrencyCode { get; set; } = "USD";
    public string? Category { get; set; }
    public string MatchStatus { get; set; } = "Unmatched";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Human-readable match explanation for scoped operators. Target database identities are omitted;
/// the server resolves the current authorized suggestion when the operator confirms it.
/// </summary>
public class OperationalBankMatchSuggestionResponse
{
    public decimal Confidence { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class ImportBankTransactionsRequest
{
    public string Provider { get; set; } = "Manual";
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? AccountMask { get; set; }
    public string? AccountType { get; set; }
    public string? AccountSubtype { get; set; }
    public IReadOnlyList<ImportBankTransactionItem> Transactions { get; set; } = [];
}

public class ImportBankTransactionItem
{
    public string ProviderTransactionId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public DateTime? AuthorizedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? MerchantName { get; set; }
    public decimal Amount { get; set; }
    public string IsoCurrencyCode { get; set; } = "USD";
    public string? Category { get; set; }
    public string? RawData { get; set; }
}

public class ImportBankTransactionsResponse
{
    public BankConnectionResponse Connection { get; set; } = new();
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public IReadOnlyList<BankTransactionResponse> Transactions { get; set; } = [];
}

public class SyncBankConnectionResponse
{
    public BankConnectionResponse Connection { get; set; } = new();
    public int ImportedCount { get; set; }
    public int SkippedCount { get; set; }
    public IReadOnlyList<BankTransactionResponse> Transactions { get; set; } = [];
}

public class MatchBankTransactionRequest
{
    public string OperationKey { get; set; } = string.Empty;
    public DateTime ExpectedUpdatedAtUtc { get; set; }
    public int? TenantAccountId { get; set; }
    public long? TenantLedgerEntryId { get; set; }
    public int? ExpenseId { get; set; }
}

public class RouteBankTransactionRequest
{
    public string OperationKey { get; set; } = string.Empty;
    public int? PropertyId { get; set; }
    public DateTime ExpectedUpdatedAtUtc { get; set; }
}

/// <summary>
/// Confirm a suggested match. Supply either a canonical tenant receipt identity
/// (<see cref="TenantAccountId"/> plus <see cref="TenantLedgerEntryId"/>) or an
/// <see cref="ExpenseId"/>; when all are omitted the current suggestion is used.
/// </summary>
public class ConfirmBankMatchRequest
{
    public string OperationKey { get; set; } = string.Empty;
    public DateTime ExpectedUpdatedAtUtc { get; set; }
    public int? TenantAccountId { get; set; }
    public long? TenantLedgerEntryId { get; set; }
    public int? ExpenseId { get; set; }
}

public class BankTransactionMutationRequest
{
    public string OperationKey { get; set; } = string.Empty;
    public DateTime ExpectedUpdatedAtUtc { get; set; }
}

/// <summary>
/// One row in the duplicate / match review queue: the imported bank line together with the
/// suggested payment or expense candidate so the landlord can confirm or dismiss the match.
/// </summary>
public class BankReviewQueueItemResponse
{
    public OperationalBankTransactionResponse Transaction { get; set; } = new();
    public OperationalBankMatchSuggestionResponse Suggestion { get; set; } = new();
}

public class BankReviewQueueResponse
{
    public int Count { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public IReadOnlyList<BankReviewQueueItemResponse> Items { get; set; } = [];
}
