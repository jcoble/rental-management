using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Screening;

public static class ApplicantScreeningQueries
{
    /// <summary>Canonical DB-side application history query; callers project before materializing.</summary>
    public static IQueryable<ApplicantScreening> ForApplication(
        this DbSet<ApplicantScreening> screenings, int portfolioId, int applicationId) =>
        screenings.AsNoTracking()
            .Where(screening => screening.PortfolioId == portfolioId
                && screening.ApplicationId == applicationId)
            .OrderByDescending(screening => screening.LastStatusAtUtc);
}
