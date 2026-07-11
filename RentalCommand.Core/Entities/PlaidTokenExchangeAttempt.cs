namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable admission and recovery record for Plaid's single-use public-token exchange. The public
/// token itself is never stored; only a lookup hash and encrypted provider receipt are persisted.
/// </summary>
public sealed class PlaidTokenExchangeAttempt
{
    public Guid Id { get; set; }
    public int PortfolioId { get; set; }
    public string ClientOperationId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string PublicTokenHash { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string? AccountMask { get; set; }
    public string? AccountType { get; set; }
    public string? AccountSubtype { get; set; }
    public string ExternalAccountIdCipherText { get; set; } = string.Empty;
    public string ExternalAccountIdHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? RemoteAdmittedAtUtc { get; set; }
    public DateTime? RemoteReceiptRecordedAtUtc { get; set; }
    public string? ProviderRequestIdentity { get; set; }
    public string? ExternalItemIdCipherText { get; set; }
    public string? ExternalItemIdHash { get; set; }
    public string? ExternalAccessTokenCipherText { get; set; }
    public int? BankConnectionId { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
