namespace RentalCommand.Core.Enums;

/// <summary>Lifecycle of a security-deposit holding: Held → PartiallyReturned / Returned / Withheld.</summary>
public enum SecurityDepositStatus
{
    Held,
    PartiallyReturned,
    Returned,
    // Deductions consumed the whole deposit — nothing was returned to the tenant. Appended last so
    // the existing ordinals (Held=0, PartiallyReturned=1, Returned=2) are unchanged.
    Withheld,
}
