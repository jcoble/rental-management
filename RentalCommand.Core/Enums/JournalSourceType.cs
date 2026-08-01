namespace RentalCommand.Core.Enums;

/// <summary>Stable source discriminator used by journal idempotency and conversion.</summary>
public enum JournalSourceType
{
    TenantCharge,
    TenantReceipt,
    ProviderSettlement,
    TenantConcession,
    ReceivableWriteOff,
    SecurityDepositReceipt,
    SecurityDepositRefund,
    SecurityDepositApplication,
    ExpensePayment,
    BillIncurred,
    BillPayment,
    BankTransfer,
    LoanPayment,
    CapitalPurchase,
    Depreciation,
    OwnerContribution,
    OwnerDistribution,
    OpeningBalance,
}
