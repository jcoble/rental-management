using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An immutable posted receivable entry. Corrections append adjustments or reversals.</summary>
public class TenantLedgerEntry : IPortfolioScoped
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public TenantLedgerEntryType EntryType { get; set; }
    public TenantLedgerDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateOnly EffectiveOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public string BusinessKey { get; set; } = string.Empty;
    /// <summary>Shared by the paired source/destination entries of one Unit transfer.</summary>
    public Guid? TransferPublicId { get; set; }
    public int? LeaseAgreementId { get; set; }
    public int? LeaseAddendumId { get; set; }
    public long? ReversesEntryId { get; set; }
    public long? RelatedTenantLedgerEntryId { get; set; }
    public DateOnly? ServicePeriodStartOn { get; set; }
    public DateOnly? ServicePeriodEndOn { get; set; }
    public long? ProviderPaymentAttemptId { get; set; }
    public int? SourceStoredFileId { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LeaseAddendum? LeaseAddendum { get; set; }
    public TenantLedgerEntry? ReversesEntry { get; set; }
    public TenantLedgerEntry? RelatedTenantLedgerEntry { get; set; }
    public TenantPaymentAttempt? ProviderPaymentAttempt { get; set; }
    public StoredFile? SourceStoredFile { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<TenantLedgerEntry> ReversalEntries { get; set; } = [];
    public List<TenantLedgerEntry> RelatedTenantLedgerEntries { get; set; } = [];
    public List<TenantLedgerAllocation> DebitAllocations { get; set; } = [];
    public List<TenantLedgerAllocation> CreditAllocations { get; set; } = [];
    public List<SecurityDepositEntry> SecurityDepositEntries { get; set; } = [];
}
