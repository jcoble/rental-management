using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>A signer identity and delivery-address snapshot frozen when its Agreement is issued.</summary>
public class LeaseAgreementSigner : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseAgreementId { get; set; }
    public int? LeaseManagementPartyId { get; set; }
    public int? TenantId { get; set; }
    public LeaseLegalSignerRole SignerRole { get; set; }
    public string NameSnapshot { get; set; } = string.Empty;
    public string EmailSnapshot { get; set; } = string.Empty;
    public short SigningOrder { get; set; }
    public bool IsRequired { get; set; } = true;

    public Portfolio? Portfolio { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LeaseManagementParty? LeaseManagementParty { get; set; }
    public Tenant? Tenant { get; set; }
}
