using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Append-only settlement link between one credit and one debit.</summary>
public class TenantLedgerAllocation : IPortfolioScoped
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public long DebitEntryId { get; set; }
    public long CreditEntryId { get; set; }
    public decimal Amount { get; set; }
    public long? ReversesAllocationId { get; set; }
    public DateTime AllocatedAtUtc { get; set; }
    public DateOnly? EffectiveOn { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public TenantLedgerEntry? DebitEntry { get; set; }
    public TenantLedgerEntry? CreditEntry { get; set; }
    public TenantLedgerAllocation? ReversesAllocation { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<TenantLedgerAllocation> ReversalAllocations { get; set; } = [];
}
