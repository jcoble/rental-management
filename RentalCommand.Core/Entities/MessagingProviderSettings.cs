namespace RentalCommand.Core.Entities;

/// <summary>Encrypted bring-your-own SMS provider credentials for one portfolio.</summary>
public sealed class MessagingProviderSettings
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string? SmsProvider { get; set; }
    public string? SmsCredentialACipherText { get; set; }
    public string? SmsCredentialBCipherText { get; set; }
    public string? SmsCredentialCCipherText { get; set; }
    public string? SmsFromNumberCipherText { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Portfolio? Portfolio { get; set; }
}
