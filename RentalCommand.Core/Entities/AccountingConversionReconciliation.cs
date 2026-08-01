using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Portfolio-level totals and exception counts for one deterministic conversion lane.</summary>
public sealed class AccountingConversionReconciliation : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public JournalSourceType SourceType { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int PostingRuleVersion { get; set; }
    public decimal SourceTotal { get; set; }
    public decimal PostedDebitTotal { get; set; }
    public decimal PostedCreditTotal { get; set; }
    public int MissingMappingCount { get; set; }
    public int UnsupportedSourceCount { get; set; }
    public decimal ImbalanceAmount { get; set; }
    public bool IsApproved { get; set; }
    public int? ApprovedOpeningBalanceJournalEntryId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public JournalEntry? ApprovedOpeningBalanceJournalEntry { get; set; }
}
