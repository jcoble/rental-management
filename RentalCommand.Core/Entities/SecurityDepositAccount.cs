using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Deposit subledger account kept separate from the tenant receivable.</summary>
public class SecurityDepositAccount : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int OriginatingAgreementId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public LeaseAgreement? OriginatingAgreement { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<SecurityDepositEntry> Entries { get; set; } = [];
}
