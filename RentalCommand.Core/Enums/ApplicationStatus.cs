namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle status of a public <see cref="Entities.RentalApplication"/>: submitted by an
/// applicant via the public no-login link, then reviewed and decided by the landlord.
/// Serialized as the string name app-wide.
/// </summary>
public enum ApplicationStatus
{
    Submitted,
    UnderReview,
    Approved,
    Declined,
    Withdrawn
}
