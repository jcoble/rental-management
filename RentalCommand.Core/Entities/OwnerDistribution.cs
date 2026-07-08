using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Cash paid out to an owner from net owner proceeds. This is not an operating expense.
/// </summary>
public class OwnerDistribution : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public DistributionMethod Method { get; set; } = DistributionMethod.Check;
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public Property? Property { get; set; }
}
