namespace RentalCommand.Core.Enums;

/// <summary>
/// The provider's (or our own decisioning's) recommendation for a completed screening: accept the
/// applicant, accept with conditions (e.g. a larger deposit / co-signer), or decline. A
/// <see cref="Decline"/> recommendation pairs with declining the application and generating an
/// FCRA adverse-action notice. Serialized as the string name app-wide.
/// </summary>
public enum ScreeningRecommendation
{
    Accept,
    Conditional,
    Decline
}
