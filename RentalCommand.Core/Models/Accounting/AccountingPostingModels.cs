using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Models.Accounting;

/// <summary>One proposed complete entry supplied by a source command.</summary>
public sealed class AccountingProposedEntry
{
    public int PortfolioId { get; set; }
    public JournalSourceType SourceType { get; set; }
    public long SourceId { get; set; }
    public string SourceBusinessKey { get; set; } = string.Empty;
    public int PostingRuleVersion { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? ReversesJournalEntryId { get; set; }
    public Guid AttemptId { get; set; }
    public int? UserId { get; set; }
    public string? ActorLabel { get; set; }
    public Guid? AuthSessionId { get; set; }
    public int? AccessContextId { get; set; }
    public Guid AtomicReceiptId { get; set; }
    public IReadOnlyList<AccountingProposedLine> Lines { get; set; } = [];
}

/// <summary>One proposed debit or credit line.</summary>
public sealed class AccountingProposedLine
{
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
}

/// <summary>Stable key for deterministic source-to-journal conversion.</summary>
public readonly record struct AccountingSourceJournalKey(
    int PortfolioId,
    JournalSourceType SourceType,
    long SourceId,
    int PostingRuleVersion);
