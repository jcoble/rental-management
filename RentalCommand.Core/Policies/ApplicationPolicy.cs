namespace RentalCommand.Core.Policies;

public static class ApplicationPolicy
{
    public const string OpenEmailUniqueConstraint =
        "IX_RentalApplications_PortfolioId_Email_Open_CI";
    public const string OpenEmailConflictMessage =
        "An open application for this email address already exists. Review it before creating another.";
}
