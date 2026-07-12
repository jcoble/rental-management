using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An immutable deposit-fund movement.</summary>
public class SecurityDepositEntry : IPortfolioScoped
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int SecurityDepositAccountId { get; set; }
    public SecurityDepositEntryType EntryType { get; set; }
    public SecurityDepositDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateOnly EffectiveOn { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? LeaseAgreementId { get; set; }
    public int? LeaseAddendumId { get; set; }
    public long? ReversesEntryId { get; set; }
    public long? TenantLedgerEntryId { get; set; }
    public int? SourceStoredFileId { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public SecurityDepositAccount? SecurityDepositAccount { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LeaseAddendum? LeaseAddendum { get; set; }
    public SecurityDepositEntry? ReversesEntry { get; set; }
    public TenantLedgerEntry? TenantLedgerEntry { get; set; }
    public StoredFile? SourceStoredFile { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<SecurityDepositEntry> ReversalEntries { get; set; } = [];
}
