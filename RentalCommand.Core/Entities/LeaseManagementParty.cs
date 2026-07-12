using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An effective-dated person's role in a LeaseManagement relationship.</summary>
public class LeaseManagementParty : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int TenantId { get; set; }
    public LeaseManagementPartyRole Role { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveThrough { get; set; }
    public bool GuarantorLegalNoticeEligible { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public Tenant? Tenant { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<TenantUserAccess> UserAccesses { get; set; } = [];
    public List<LeaseAgreementSigner> AgreementSignerSnapshots { get; set; } = [];
    public List<LeaseAddendumSigner> AddendumSignerSnapshots { get; set; } = [];
}
