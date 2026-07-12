using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Explicit portal access for a user through one effective household party.</summary>
public class TenantUserAccess : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int ApplicationUserId { get; set; }
    public int LeaseManagementPartyId { get; set; }
    public DateTime GrantedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int GrantedByUserId { get; set; }
    public int? RevokedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public Portfolio? Portfolio { get; set; }
    public ApplicationUser? ApplicationUser { get; set; }
    public LeaseManagementParty? LeaseManagementParty { get; set; }
    public ApplicationUser? GrantedByUser { get; set; }
    public ApplicationUser? RevokedByUser { get; set; }
}
