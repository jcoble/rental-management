using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Token-fenced provider attempt; a successful charge/refund produces one ledger entry.</summary>
public class TenantPaymentAttempt : IAuditable, IPortfolioScoped
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    /// <summary>
    /// Immutable tenant-selected debit that a Charge attempt must settle before any remainder
    /// can remain unapplied. Null for explicitly typed non-targeted receipt attempts, legacy
    /// targetless attempts, refunds, and verifications.
    /// </summary>
    public long? ChargeLedgerEntryId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderObjectId { get; set; }
    /// <summary>The settled provider Charge attempt whose receipt this Refund returns.</summary>
    public long? RefundsPaymentAttemptId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public TenantPaymentAttemptType AttemptType { get; set; }
    public TenantPaymentAttemptState State { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? PaymentMethodSummary { get; set; }
    public string? PayerName { get; set; }
    public string? CheckNumber { get; set; }
    public string? BankName { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? SettledAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? ClaimOwner { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }
    /// <summary>
    /// Durable charge-level provider fence. Unlike ClaimToken, this survives the prepare
    /// transaction and the provider call boundary. A provider caller must present it when it
    /// advances the attempt to Submitted or binds the provider object.
    /// </summary>
    public Guid? ProviderFenceToken { get; set; }
    public DateTime? ProviderFenceAcquiredAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public TenantLedgerEntry? ChargeLedgerEntry { get; set; }
    public TenantLedgerEntry? LedgerEntry { get; set; }
    public TenantPaymentAttempt? RefundsPaymentAttempt { get; set; }
    public List<TenantPaymentAttempt> RefundAttempts { get; set; } = [];
}
