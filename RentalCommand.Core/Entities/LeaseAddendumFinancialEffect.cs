using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>A queryable billing or deposit effect frozen with its issued Addendum.</summary>
public class LeaseAddendumFinancialEffect : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseAddendumId { get; set; }
    public LeaseAddendumFinancialEffectType EffectType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ChargeCode { get; set; } = string.Empty;
    public DateOnly? EffectiveFromOn { get; set; }
    public DateOnly? EffectiveThroughOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public string Description { get; set; } = string.Empty;

    public Portfolio? Portfolio { get; set; }
    public LeaseAddendum? LeaseAddendum { get; set; }
}
