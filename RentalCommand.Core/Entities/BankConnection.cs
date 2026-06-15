namespace RentalCommand.Core.Entities;

public class BankConnection
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Provider { get; set; } = "Plaid";
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? AccountMask { get; set; }
    public string? AccountType { get; set; }
    public string? AccountSubtype { get; set; }
    public string? ExternalItemIdCipherText { get; set; }
    public string? ExternalAccountIdCipherText { get; set; }
    public string? ExternalAccessTokenCipherText { get; set; }
    public string? SyncCursorCipherText { get; set; }
    /// <summary>
    /// Connection lifecycle ("Active", etc.). This — not a soft-delete flag — is the real lifecycle
    /// for a bank connection (audit M-8): the previously-present <c>DeletedAt</c> column was never
    /// written and had no query filter, so it was dead schema and has been removed.
    /// </summary>
    public string Status { get; set; } = "Active";
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public ICollection<BankTransaction> Transactions { get; set; } = new List<BankTransaction>();
}
