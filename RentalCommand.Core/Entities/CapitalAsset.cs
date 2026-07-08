using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;

namespace RentalCommand.Core.Entities;

public class CapitalAsset : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? SourceExpenseId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal CostBasis { get; set; }
    public DateTime InServiceDate { get; set; }
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;
    public decimal RecoveryYears { get; set; } = RecoveryClass.ResidentialBuilding;
    public DepreciationConvention Convention { get; set; } = DepreciationConvention.MidMonth;
    public decimal AccumulatedDepreciation { get; set; }
    public DateTime? DisposedOnDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public Expense? SourceExpense { get; set; }
}
