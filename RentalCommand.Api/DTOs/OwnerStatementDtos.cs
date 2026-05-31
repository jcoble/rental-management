namespace RentalCommand.Api.DTOs;

/// <summary>A single property line in an owner statement: income, expenses, management fee, and net.</summary>
public record OwnerStatementPropertyLine(
    int PropertyId,
    string PropertyName,
    decimal RentalIncome,
    decimal Expenses,
    decimal ManagementFee,
    decimal NetToOwner);

/// <summary>
/// Full owner statement for a given owner and tax year: one line per property plus portfolio-level totals.
/// </summary>
public class OwnerStatementReport
{
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public int Year { get; set; }
    public IReadOnlyList<OwnerStatementPropertyLine> Properties { get; set; } = [];
    public decimal TotalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal TotalManagementFee { get; set; }
    public decimal TotalNetToOwner { get; set; }
}

/// <summary>Lightweight summary for the owner picker/list: owner id, name, and year net distribution.</summary>
public record OwnerStatementSummary(int OwnerId, string OwnerName, decimal NetToOwner);
