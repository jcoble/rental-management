namespace RentalCommand.Core.Enums;

public enum ApplicationFinancialEntryType
{
    FeeCollection,
    Refund,
    Adjustment,
}

public enum ApplicationFinancialDirection
{
    Increase,
    Decrease,
}

public enum ApplicationFinancialEntrySource
{
    Manual,
    Scan,
    PaymentProvider,
    Import,
    System,
}
