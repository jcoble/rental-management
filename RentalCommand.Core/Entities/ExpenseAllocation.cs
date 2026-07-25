using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One positive, typed share of an Expense. PostgreSQL validates the final nonempty set against the
/// parent amount at transaction commit so an allocation replacement cannot expose a partial total.
/// </summary>
public class ExpenseAllocation : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int ExpenseId { get; set; }
    public ExpenseAllocationTargetKind TargetKind { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? OwnerEntityId { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }

    public Expense? Expense { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
}
