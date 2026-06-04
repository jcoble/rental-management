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
    public int? MatchedPaymentId { get; set; }
    public int? MatchedExpenseId { get; set; }
    public string MatchStatus { get; set; } = "Unmatched";
    public decimal? MatchConfidence { get; set; }
    public string? Notes { get; set; }
    public BankMatchSuggestionResponse? SuggestedMatch { get; set; }
}

public class BankMatchSuggestionResponse
{
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
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
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
}

/// <summary>
/// Confirm a suggested match. Supply exactly one of <see cref="PaymentId"/> or
/// <see cref="ExpenseId"/>; when both are omitted the transaction's current suggestion is used.
/// </summary>
public class ConfirmBankMatchRequest
{
    public int? PaymentId { get; set; }
    public int? ExpenseId { get; set; }
}

/// <summary>
/// One row in the duplicate / match review queue: the imported bank line together with the
/// suggested payment or expense candidate so the landlord can confirm or dismiss the match.
/// </summary>
public class BankReviewQueueItemResponse
{
    public BankTransactionResponse Transaction { get; set; } = new();
    public BankMatchSuggestionResponse Suggestion { get; set; } = new();
}

public class BankReviewQueueResponse
{
    public int Count { get; set; }
    public IReadOnlyList<BankReviewQueueItemResponse> Items { get; set; } = [];
}
