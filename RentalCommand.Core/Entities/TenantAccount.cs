using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Continuous tenant receivable account owned by one LeaseManagement.</summary>
public class TenantAccount : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? CloseReasonCode { get; set; }
    public string? CloseNote { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<TenantAccountConditionPeriod> ConditionPeriods { get; set; } = [];
    public List<TenantLedgerEntry> LedgerEntries { get; set; } = [];
    public List<TenantLedgerAllocation> LedgerAllocations { get; set; } = [];
    public List<TenantPaymentAttempt> PaymentAttempts { get; set; } = [];
    public List<TenantAutopayEnrollment> AutopayEnrollments { get; set; } = [];
    public SecurityDepositAccount? SecurityDepositAccount { get; set; }
}
