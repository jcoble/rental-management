namespace RentalCommand.Core.Enums;

/// <summary>
/// Status of a single generated <see cref="Entities.LoanPayment"/> in the amortization schedule.
/// The worker writes rows as <see cref="Scheduled"/>; marking one <see cref="Paid"/> records that
/// the debt-service cash actually went out (it does not change the amortized split).
/// </summary>
public enum LoanPaymentStatus
{
    Scheduled,
    Paid,
}
