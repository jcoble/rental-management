namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due <see cref="Core.Entities.RecurringExpense"/> templates into
/// <see cref="Core.Entities.Expense"/> rows, idempotently (by schedule advancement — one expense per
/// template per period), so standing costs flow into every report without re-entry.
/// </summary>
public interface IRecurringExpenseGenerationService
{
    /// <summary>
    /// For each active, non-deleted recurring-expense template whose NextRunDate has arrived, create
    /// the expense row(s) for every due period (catching up missed periods so deductions are not
    /// understated) and advance NextRunDate past today. Returns the number of expenses created.
    /// </summary>
    Task<int> GenerateAsync(CancellationToken ct = default);
}
