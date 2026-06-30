namespace RentalCommand.Core.Enums;

public enum RentTrackingStartMode
{
    BackfillFromLeaseStart,
    ForwardOnly,
    CustomCutoffDate,
    OpeningBalanceOnly
}
