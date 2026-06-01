namespace RentalCommand.Core.Enums;

/// <summary>Lifecycle of a security-deposit holding: Held → PartiallyReturned / Returned.</summary>
public enum SecurityDepositStatus
{
    Held,
    PartiallyReturned,
    Returned,
}
