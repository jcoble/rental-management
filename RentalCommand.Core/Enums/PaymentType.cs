namespace RentalCommand.Core.Enums;

public enum PaymentType
{
    Rent,
    SecurityDeposit,
    LateFee,
    Utility,
    Other,

    // Append-only: the ordinal backs stored ints and the vw_accounting_transactions CASE map (0-4).
    // A real application/screening fee — income recorded before any lease exists (see Payment.ApplicationId).
    ApplicationFee = 5,
}
