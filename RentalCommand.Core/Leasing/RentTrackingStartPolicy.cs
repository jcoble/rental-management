using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>Resolves the reviewed rent-history choice to the account's durable start date.</summary>
public static class RentTrackingStartPolicy
{
    public static DateOnly? Resolve(
        DateOnly leaseStart,
        RentTrackingStartMode mode,
        DateOnly? requestedStart,
        DateOnly businessDate) =>
        mode switch
        {
            RentTrackingStartMode.BackfillFromLeaseStart => null,
            RentTrackingStartMode.ForwardOnly => Max(leaseStart, businessDate),
            RentTrackingStartMode.CustomCutoffDate when requestedStart.HasValue =>
                Max(leaseStart, requestedStart.Value),
            RentTrackingStartMode.CustomCutoffDate =>
                throw new ArgumentException("Rent tracking cutoff date is required."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), "Rent tracking start mode is invalid."),
        };

    private static DateOnly Max(DateOnly left, DateOnly right) => left >= right ? left : right;
}
