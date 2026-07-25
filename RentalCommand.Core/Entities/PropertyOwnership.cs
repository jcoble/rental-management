using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Effective-dated legal ownership of one Property by one OwnerEntity. Statement and payee
/// identities are relationship facts because the same OwnerEntity can use different recipients
/// or payees for different Properties.
/// </summary>
public sealed class PropertyOwnership : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int OwnerEntityId { get; set; }
    public decimal OwnershipSharePercent { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public string StatementRecipientName { get; set; } = string.Empty;
    public string? StatementRecipientEmail { get; set; }
    public string PayeeName { get; set; } = string.Empty;

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
}
