using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Leasing;

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
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber)),
                MatchedCount = relationship.Addenda.Count(addendum =>
                    series.Contains(addendum.SeriesPublicId)
                    && addendum.FullyExecutedAtUtc != null && addendum.VoidedAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber)),
            });

        var sql = query.ToQueryString();
        sql.Should().StartWith("-- @");
        sql.Should().Contain("SELECT");
        sql.Should().Contain("count(*)::int");
        sql.Should().Contain("\"LeaseAddenda\"");
        sql.Should().NotContain("ClientEvaluation");
    }

    [Fact]
    public void Signer_scope_sql_requires_the_exact_party_tenant_pair()
    {
        var sql = PrivateSql("ValidateAgreementDraftSignerScopeSql");

        sql.Should().Contain("party.\"TenantId\" <> input.tenant_id");
        sql.Should().Contain("(input.lease_management_party_id IS NULL) <> (input.tenant_id IS NULL)");
        sql.Should().Contain("party.\"LeaseManagementId\" = @leaseManagementId");
        sql.Should().Contain("party.\"PortfolioId\" = @portfolioId");
    }

    [Fact]
    public void Reissue_sql_creates_successor_bound_drafts_and_copies_children()
    {
        var sql = PrivateSql("CreateRenewalAddendumDraftsSql");

        sql.Should().Contain("INSERT INTO \"LeaseAddenda\"");
        sql.Should().Contain("@renewalAgreementId");
        sql.Should().Contain("source.\"Id\"");
        sql.Should().Contain("INSERT INTO \"LeaseAddendumSigners\"");
        sql.Should().Contain("INSERT INTO \"LeaseAddendumFinancialEffects\"");
        sql.Should().Contain("replacement.\"Id\", @createdAt");
        sql.Should().NotContain("@replacementAddendumId");
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
        sql.Should().Contain("FROM vw_lease_agreement_status");
        sql.Should().Contain("\"PortfolioId\" = 7");
        sql.Should().Contain("\"LeaseManagementId\" = 19");
        sql.Should().Contain("\"AgreementId\" = 23");
        sql.Should().Contain("\"IsGoverning\"");
    }

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);

    private static string PrivateSql(string fieldName) =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException($"Missing SQL constant {fieldName}."));
}
