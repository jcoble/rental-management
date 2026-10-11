using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An append-oriented effective period for a durable account condition.</summary>
public class TenantAccountConditionPeriod : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public TenantAccountCondition Condition { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
