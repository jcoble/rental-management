using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Screening;

public static class ApplicantScreeningQueries
{
    /// <summary>Canonical DB-side application history query; callers project before materializing.</summary>
    public static IQueryable<ApplicantScreening> ForApplication(
        this DbSet<ApplicantScreening> screenings, int portfolioId, int applicationId) =>
        screenings.AsNoTracking().ForApplication(portfolioId, applicationId);

    public static IQueryable<ApplicantScreening> ForApplication(
        this IQueryable<ApplicantScreening> screenings, int portfolioId, int applicationId) =>
        screenings
            .Where(screening => screening.PortfolioId == portfolioId
                && screening.ApplicationId == applicationId)
            .OrderByDescending(screening => screening.LastStatusAtUtc);

    /// <summary>
    /// Keeps current-session authorization correlated to the screening query. No application ids
    /// are materialized before the database filters and sorts the workspace history.
    /// </summary>
    public static IQueryable<ApplicantScreening> ForAuthorizedApplication(
        this DbSet<ApplicantScreening> screenings,
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        int applicationId,
        DateTime securityNowUtc)
    {
        var authorizedApplications = db.RentalApplications.AsNoTracking()
            .WhereAuthorized(
                db,
                scope,
                [CapabilityKeys.LeasingApplicationsManage],
                securityNowUtc);

        return screenings.AsNoTracking()
            .Where(screening => screening.PortfolioId == scope.PortfolioId
                && screening.ApplicationId == applicationId
                && authorizedApplications.Any(application => application.Id == screening.ApplicationId))
            .OrderByDescending(screening => screening.LastStatusAtUtc);
    }

    /// <summary>Canonical SQL-translated API/receipt projection.</summary>
    public static IQueryable<ApplicantScreeningSnapshot> ProjectSnapshots(
        this IQueryable<ApplicantScreening> screenings) =>
        screenings.Select(screening => new ApplicantScreeningSnapshot(
            screening.Id,
            screening.ApplicationId,
            screening.Mode,
            screening.Status,
            screening.ProviderDisplayName,
            screening.ProviderReference,
            screening.ProviderHostedUrl,
            screening.ConsentConfirmed,
            screening.InvitedAtUtc,
            screening.ApplicantSubmittedAtUtc,
            screening.CompletedAtUtc,
            screening.FailedAtUtc,
            screening.LastStatusAtUtc,
            screening.Decision,
            screening.DecisionReason,
            screening.ConsumerReportUsedForDecision,
            screening.CreditReportingAgencyName,
            screening.CreditReportingAgencyAddress,
            screening.CreditReportingAgencyPhone,
            screening.CreditReportingAgencyName != null
                && screening.CreditReportingAgencyAddress != null
                && screening.CreditReportingAgencyPhone != null,
            screening.Status == ApplicantScreeningStatus.Completed
                && screening.Decision == ScreeningDecision.Decline
                && screening.ConsumerReportUsedForDecision
                && screening.CreditReportingAgencyName != null
                && screening.CreditReportingAgencyAddress != null
                && screening.CreditReportingAgencyPhone != null,
            screening.Status == ApplicantScreeningStatus.Created
                ? "The screening record is ready to start."
                : screening.Status == ApplicantScreeningStatus.AwaitingProvider
                    ? "Rental Command is creating the secure provider invitation."
                    : screening.Status == ApplicantScreeningStatus.AwaitingApplicant
                        ? "The invitation was created and is waiting for the applicant."
                        : screening.Status == ApplicantScreeningStatus.InProgress
                            ? "The applicant submitted their information and the provider is processing it."
                            : screening.Status == ApplicantScreeningStatus.Completed
                                ? "The provider has completed the screening."
                                : screening.Status == ApplicantScreeningStatus.Failed
                                    ? "The screening could not be completed."
                                    : "The screening was cancelled.",
            screening.Status == ApplicantScreeningStatus.Created
                ? "Start the screening when the applicant is ready."
                : screening.Status == ApplicantScreeningStatus.AwaitingProvider
                    ? "No action is needed yet. Retry with the same request if this does not update."
                    : screening.Status == ApplicantScreeningStatus.AwaitingApplicant
                        ? "Ask the applicant to complete the secure provider invitation."
                        : screening.Status == ApplicantScreeningStatus.InProgress
                            ? "Wait for the provider to finish, or open the provider site for details."
                            : screening.Status == ApplicantScreeningStatus.Completed && screening.Decision == null
                                ? "Review the result at the provider and record your screening decision."
                                : screening.Status == ApplicantScreeningStatus.Completed
                                    ? "The screening decision is recorded."
                                    : screening.Status == ApplicantScreeningStatus.Failed
                                        && screening.Mode == ScreeningMode.Integrated
                                            ? "Retry the integrated screening or track an outside screening."
                                            : screening.Status == ApplicantScreeningStatus.Failed
                                                ? "Update this outside screening or start another one."
                                                : "Start another screening if it is still needed.",
            screening.Status == ApplicantScreeningStatus.Completed
                || screening.Status == ApplicantScreeningStatus.Failed
                || screening.Status == ApplicantScreeningStatus.Cancelled,
            screening.ProviderHostedUrl != null));

    public static Task<ApplicantScreeningSnapshot> LoadSnapshotAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int screeningId,
        CancellationToken ct) =>
        persistence.Query<ApplicantScreening>().AsNoTracking()
            .Where(screening => screening.Id == screeningId && screening.PortfolioId == portfolioId)
            .ProjectSnapshots()
            .SingleAsync(ct);
}
