using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Explicit tenant-portal access rooted in one workspace access context and one household party.
/// The context, not a legacy tenant/user shortcut, is the authorization subject.
/// </summary>
public class TenantUserAccess : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int AccessContextId { get; set; }
    public int ApplicationUserId { get; set; }
    public int LeaseManagementPartyId { get; set; }
    public DateTime GrantedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int GrantedByUserId { get; set; }
    public int? RevokedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public Portfolio? Portfolio { get; set; }
    public WorkspaceAccessContext? AccessContext { get; set; }
    public ApplicationUser? ApplicationUser { get; set; }
    public LeaseManagementParty? LeaseManagementParty { get; set; }
    public ApplicationUser? GrantedByUser { get; set; }
    public ApplicationUser? RevokedByUser { get; set; }
}
