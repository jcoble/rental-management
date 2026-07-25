namespace RentalCommand.Core.Enums;

/// <summary>The one operational context in which an expense occurred.</summary>
public enum ExpenseOperationalScope
{
    Portfolio = 0,
    Property = 1,
    Unit = 2,
    WorkOrder = 3,
}
