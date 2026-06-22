using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class SecurityDepositHolding
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public decimal Amount { get; set; }                 // original deposit held
    public SecurityDepositStatus Status { get; set; } = SecurityDepositStatus.Held;
    public DateTime HeldAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public decimal? ReturnedAmount { get; set; }
    public string DeductionsJson { get; set; } = "[]";   // jsonb: [{ reason, amount, notes? }]
    public decimal DeductionsTotal { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Lease? Lease { get; set; }
    public Portfolio? Portfolio { get; set; }
}
