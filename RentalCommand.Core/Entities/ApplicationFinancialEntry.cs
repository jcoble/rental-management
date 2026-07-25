using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One immutable application-finance fact. Corrections append a refund or adjustment; a posted
/// entry is never edited or deleted.
/// </summary>
public sealed class ApplicationFinancialEntry : IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int ApplicationFinancialAccountId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public ApplicationFinancialEntryType EntryType { get; set; }
    public ApplicationFinancialDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateOnly EffectiveOn { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Provider { get; set; }
    public string? ProviderReference { get; set; }
    public ApplicationFinancialEntrySource Source { get; set; }
    public string? SourceReference { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public int? RelatedEntryId { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public ApplicationFinancialAccount? ApplicationFinancialAccount { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public ApplicationFinancialEntry? RelatedEntry { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<ApplicationFinancialEntry> RelatedEntries { get; set; } = [];
}
