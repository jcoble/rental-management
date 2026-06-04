namespace RentalCommand.Core.Enums;

/// <summary>
/// How often a <see cref="Entities.RecurringMaintenanceTask"/> repeats. The Engine advances the
/// task's NextDueDate by this interval each time it generates a work order (Weekly = +7 days,
/// Monthly = +1 month, Quarterly = +3 months, SemiAnnually = +6 months, Annually = +1 year).
/// </summary>
public enum RecurrenceInterval
{
    Weekly,
    Monthly,
    Quarterly,
    SemiAnnually,
    Annually,
}
