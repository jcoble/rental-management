namespace RentalCommand.Core.Entities;

/// <summary>One immutable debit or credit line belonging to a posted journal entry.</summary>
public sealed class JournalLine
{
    public int Id { get; set; }
    public int JournalEntryId { get; set; }
    public int LedgerAccountId { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public string? Memo { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? TenantAccountId { get; set; }
    public int? OwnerEntityId { get; set; }
    public string? SourceLineType { get; set; }
    public long? SourceLineId { get; set; }

    public JournalEntry? JournalEntry { get; set; }
    public LedgerAccount? LedgerAccount { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
}
