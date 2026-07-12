using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Tests;

/// <summary>
/// Translation guards for the two database-side eligibility projections used by Agreement draft
/// commands. The behavioral/concurrency twin belongs in the PostgreSQL integration lane.
/// </summary>
public sealed class LeaseAgreementDraftSqlTranslationTests
{
    [Fact]
    public void Renewal_addendum_coverage_is_one_server_side_aggregate_statement()
    {
        using var db = Context();
        var portfolioId = 7;
        var leaseManagementId = 19;
        var businessDate = new DateOnly(2026, 7, 12);
        var series = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var replacementIds = new[] { 31, 32 };

        var query = db.LeaseManagements
            .Where(relationship => relationship.Id == leaseManagementId
                && relationship.PortfolioId == portfolioId)
            .Select(relationship => new
            {
                EffectiveCount = relationship.Addenda.Count(addendum =>
                    addendum.FullyExecutedAtUtc != null && addendum.VoidedAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)),
                MatchedCount = relationship.Addenda.Count(addendum =>
                    series.Contains(addendum.SeriesPublicId)
                    && addendum.FullyExecutedAtUtc != null && addendum.VoidedAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)),
                ReplacementCount = relationship.Addenda.Count(addendum =>
                    replacementIds.Contains(addendum.Id)),
            });

        var sql = query.ToQueryString();
        sql.Should().StartWith("-- @__");
        sql.Should().Contain("SELECT");
        sql.Should().Contain("count(*)::int");
        sql.Should().Contain("\"LeaseAddenda\"");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Current_governing_source_is_filtered_in_postgresql()
    {
        using var db = Context();
        var query = db.LeaseAgreementStatusProjections.Where(status =>
            status.PortfolioId == 7
            && status.LeaseManagementId == 19
            && status.AgreementId == 23
            && status.IsGoverning);

        var sql = query.ToQueryString();
        sql.Should().Contain("\"vw_lease_agreement_status\"");
        sql.Should().Contain("\"PortfolioId\" = 7");
        sql.Should().Contain("\"LeaseManagementId\" = 19");
        sql.Should().Contain("\"AgreementId\" = 23");
        sql.Should().Contain("\"IsGoverning\"");
    }

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);
}
