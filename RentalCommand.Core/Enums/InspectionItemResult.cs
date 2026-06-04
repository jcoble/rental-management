namespace RentalCommand.Core.Enums;

/// <summary>
/// Per-item outcome on an inspection checklist. <see cref="Pending"/> is the materialized default
/// (not yet walked); <see cref="Fail"/> items spawn a work order on completion.
/// </summary>
public enum InspectionItemResult
{
    Pending,
    Pass,
    Fail,
    NotApplicable
}
