namespace RentalCommand.Core.Enums;

/// <summary>
/// How often a <see cref="Entities.RecurringExpense"/> template materializes into an
/// <see cref="Entities.Expense"/> row. The Engine advances the template's NextRunDate by this
/// interval each time it generates an expense (Monthly = +1 month, Quarterly = +3 months,
/// Annual = +1 year).
/// </summary>
public enum RecurringExpenseFrequency
{
    Monthly,
    Quarterly,
    Annual,
}
