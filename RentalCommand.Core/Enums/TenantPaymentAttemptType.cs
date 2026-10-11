namespace RentalCommand.Core.Enums;

public enum TenantPaymentAttemptType
{
    Charge,
    UnappliedReceipt,
    DepositReceipt,
    ImportedReceipt,
    LegacyTargetlessCharge,
    Refund,
    Verification,
}
