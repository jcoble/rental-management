namespace RentalCommand.Core.Enums;

/// <summary>
/// What the autopilot does automatically when a lease nears its end. Lease-end notices
/// (renewal / month-to-month / non-renewal) are mutually exclusive for a given lease, so this is a
/// single choice rather than independent per-type toggles. <see cref="Draft"/> (default) auto-sends
/// nothing — drafts still appear in the notices queue for manual approval.
/// </summary>
public enum LeaseEndAutoAction
{
    Draft = 0,
    Renewal = 1,
    MonthToMonth = 2,
    NonRenewal = 3,
}
