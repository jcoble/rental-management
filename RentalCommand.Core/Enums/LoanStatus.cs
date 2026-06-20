namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle of a per-property <see cref="Entities.Loan"/> (mortgage). The
/// <c>DebtServiceWorker</c> only generates monthly payments for an <see cref="Active"/> loan;
/// it flips the loan to <see cref="PaidOff"/> when the final scheduled payment zeroes the balance.
/// <see cref="Closed"/> is a manual "no longer track this" state (refinanced, sold, etc.).
/// </summary>
public enum LoanStatus
{
    Active,
    PaidOff,
    Closed,
}
