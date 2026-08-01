using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Monthly tenant obligation that is materialized as a separate source charge.</summary>
public sealed class RecurringTenantCharge : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseAgreementId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int LedgerAccountId { get; set; }
    public DateOnly EffectiveStartOn { get; set; }
    public DateOnly? EffectiveEndOn { get; set; }
    public short MonthlyDueDay { get; set; }
    public DateOnly NextRunDate { get; set; }
    public bool IsActive { get; set; } = true;
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LedgerAccount? LedgerAccount { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
}
