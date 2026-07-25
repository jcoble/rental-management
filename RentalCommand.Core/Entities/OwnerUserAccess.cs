using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Effective, explicitly granted owner-portal relationship. The composite context/user/workspace
/// foreign key proves the relationship belongs to the same signed-in identity and workspace without
/// making a legacy user column the authorization source.
/// </summary>
public sealed class OwnerUserAccess : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int AccessContextId { get; set; }
    public int ApplicationUserId { get; set; }
    public int OwnerEntityId { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public DateTime GrantedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public int GrantedByUserId { get; set; }
    public int? RevokedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;

    public Portfolio? Portfolio { get; set; }
    public WorkspaceAccessContext? AccessContext { get; set; }
    public ApplicationUser? ApplicationUser { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public ApplicationUser? GrantedByUser { get; set; }
    public ApplicationUser? RevokedByUser { get; set; }
}
