namespace RentalCommand.Api.DTOs;

/// <summary>A single IRS Schedule E expense category and its summed amount for a given year.</summary>
public record ScheduleECategoryAmount(string Category, decimal Amount);

/// <summary>
/// Year-end income/expense summary for a single property, structured for IRS Schedule E reporting.
/// </summary>
public record ScheduleEPropertyReport(
    int PropertyId,
    string PropertyName,
    decimal RentalIncome,
    IReadOnlyList<ScheduleECategoryAmount> ExpensesByCategory,
    decimal TotalExpenses,
    decimal NetIncome);  // RentalIncome - TotalExpenses

/// <summary>
/// Full Schedule E report for a portfolio for a given tax year: one row per property that had
/// any rental income or deductible expense, plus portfolio-level grand totals.
/// </summary>
public class ScheduleEReport
{
    public int Year { get; set; }
    public IReadOnlyList<ScheduleEPropertyReport> Properties { get; set; } = [];
    public decimal TotalRentalIncome { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal NetIncome { get; set; }
}
