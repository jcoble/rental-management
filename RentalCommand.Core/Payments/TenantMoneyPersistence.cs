using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Payments;

public sealed class TenantMoneyScheduledCharge
{
    public long LedgerEntryId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseAgreementId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateOnly DueOn { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
}

public sealed class TenantMoneyAllocationSummary
{
    public int AllocationCount { get; set; }
    public decimal AllocatedAmount { get; set; }
}

public sealed class TenantMoneyOpeningSecurityDepositRecovery
{
    public bool IsValid { get; set; }
    public int AccountCount { get; set; }
    public int CreatedAccountCount { get; set; }
    public int CreatedEntryCount { get; set; }
    public decimal ReconciledTotal { get; set; }
    public string ValidationError { get; set; } = string.Empty;
}

public sealed class TenantMoneyHistoricalRentChargeRecovery
{
    public bool IsValid { get; set; }
    public long ReversedRentChargeEntryId { get; set; }
    public long ReversalEntryId { get; set; }
    public long ReplacementRentChargeEntryId { get; set; }
    public long ReceiptEntryId { get; set; }
    public long ReversedAllocationId { get; set; }
    public long ReplacementAllocationId { get; set; }
    public DateOnly RentTrackingStartOn { get; set; }
    public decimal ReversedRentAmount { get; set; }
    public decimal ReplacementRentAmount { get; set; }
    public decimal ReallocatedAmount { get; set; }
    public string ValidationError { get; set; } = string.Empty;
}

public sealed class TenantMoneyRefundedAllocationRecovery
{
    public bool IsValid { get; set; }
    public long ReversedAllocationId { get; set; }
    public long ReversalAllocationId { get; set; }
    public long DebitEntryId { get; set; }
    public long CreditEntryId { get; set; }
    public long RefundPaymentAttemptId { get; set; }
    public decimal ReversedAmount { get; set; }
    public string ValidationError { get; set; } = string.Empty;
}

public sealed class TenantMoneyLateFeeChargeRecovery
{
    public bool IsValid { get; set; }
    public int ReversedChargeCount { get; set; }
    public int ReplacementChargeCount { get; set; }
    public int ReversedAllocationCount { get; set; }
    public int ReplacementAllocationCount { get; set; }
    public decimal ReversedTotal { get; set; }
    public decimal ReplacementTotal { get; set; }
    public decimal ReversedAllocationTotal { get; set; }
    public decimal ReplacementAllocationTotal { get; set; }
    public string ValidationError { get; set; } = string.Empty;
}
