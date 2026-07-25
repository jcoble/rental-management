namespace RentalCommand.Core.Entities;

public class BankTransaction
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int BankConnectionId { get; set; }
    /// <summary>
    /// Explicit operational route selected by a workspace bank administrator. A null route keeps
    /// the line in the bank-admin experience only; property-scoped operators never infer access
    /// from amount, date, or a suggested match.
    /// </summary>
    public int? PropertyId { get; set; }
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
    public string? RawData { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public BankConnection? BankConnection { get; set; }
    public Property? Property { get; set; }
    public TenantAccount? MatchedTenantAccount { get; set; }
    public TenantLedgerEntry? MatchedTenantLedgerEntry { get; set; }
    public Expense? MatchedExpense { get; set; }
}
