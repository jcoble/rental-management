using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Atomic;
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
                    addendum.FullyExecutedAtUtc != null
                    && addendum.ExecutedArtifactId != null
                    && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null
                    && addendum.BaseAgreement != null
                    && addendum.BaseAgreement.FullyExecutedAtUtc != null
                    && addendum.BaseAgreement.ExecutedArtifactId != null
                    && addendum.BaseAgreement.VoidedAtUtc == null
                    && addendum.BaseAgreement.DraftCanceledAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber
                        && newer.FullyExecutedAtUtc != null
                        && newer.ExecutedArtifactId != null
                        && newer.VoidedAtUtc == null
                        && newer.DraftCanceledAtUtc == null
                        && newer.EffectiveFromOn <= businessDate
                        && (newer.EffectiveThroughOn == null
                            || newer.EffectiveThroughOn >= businessDate)
                        && (newer.SupersededEffectiveOn == null
                            || newer.SupersededEffectiveOn > businessDate)
                        && newer.BaseAgreement != null
                        && newer.BaseAgreement.FullyExecutedAtUtc != null
                        && newer.BaseAgreement.ExecutedArtifactId != null
                        && newer.BaseAgreement.VoidedAtUtc == null
                        && newer.BaseAgreement.DraftCanceledAtUtc == null)),
                MatchedCount = relationship.Addenda.Count(addendum =>
                    series.Contains(addendum.SeriesPublicId)
                    && addendum.FullyExecutedAtUtc != null
                    && addendum.ExecutedArtifactId != null
                    && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null
                    && addendum.BaseAgreement != null
                    && addendum.BaseAgreement.FullyExecutedAtUtc != null
                    && addendum.BaseAgreement.ExecutedArtifactId != null
                    && addendum.BaseAgreement.VoidedAtUtc == null
                    && addendum.BaseAgreement.DraftCanceledAtUtc == null
                    && addendum.EffectiveFromOn <= businessDate
                    && (addendum.EffectiveThroughOn == null || addendum.EffectiveThroughOn >= businessDate)
                    && (addendum.SupersededEffectiveOn == null
                        || addendum.SupersededEffectiveOn > businessDate)
                    && !relationship.Addenda.Any(newer =>
                        newer.SeriesPublicId == addendum.SeriesPublicId
                        && newer.VersionNumber > addendum.VersionNumber
                        && newer.FullyExecutedAtUtc != null
                        && newer.ExecutedArtifactId != null
                        && newer.VoidedAtUtc == null
                        && newer.DraftCanceledAtUtc == null
                        && newer.EffectiveFromOn <= businessDate
                        && (newer.EffectiveThroughOn == null
                            || newer.EffectiveThroughOn >= businessDate)
                        && (newer.SupersededEffectiveOn == null
                            || newer.SupersededEffectiveOn > businessDate)
                        && newer.BaseAgreement != null
                        && newer.BaseAgreement.FullyExecutedAtUtc != null
                        && newer.BaseAgreement.ExecutedArtifactId != null
                        && newer.BaseAgreement.VoidedAtUtc == null
                        && newer.BaseAgreement.DraftCanceledAtUtc == null)),
            });

        var sql = query.ToQueryString();
        sql.Should().StartWith("-- @");
        sql.Should().Contain("SELECT");
        sql.Should().Contain("count(*)::int");
        sql.Should().Contain("\"LeaseAddenda\"");
        sql.Should().Contain("\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().Contain("\"ExecutedArtifactId\" IS NOT NULL");
        sql.Should().Contain("\"DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("\"LeaseAgreements\"");
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
        sql.Should().Contain("INNER JOIN \"LeaseAgreements\" AS newer_base");
        sql.Should().Contain("INNER JOIN \"LeaseAgreements\" AS source_base");
        sql.Should().Contain("source.\"ExecutedArtifactId\" IS NOT NULL");
        sql.Should().Contain("source_base.\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().Contain("newer.\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().Contain("newer.\"ExecutedArtifactId\" IS NOT NULL");
        sql.Should().Contain("newer.\"DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("newer_base.\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().Contain("max(existing.\"VersionNumber\") + 1 AS next_version_number");
        sql.Should().Contain("@renewalAgreementId, next_versions.next_version_number");
        sql.Should().NotContain("@replacementAddendumId");
    }

    [Fact]
    public void Legal_execution_transition_is_one_set_based_database_mutation()
    {
        var sql = PrivateSql("ExecuteLegalArtifactTransitionSql");

        sql.Should().Contain("target_agreement AS MATERIALIZED");
        sql.Should().Contain("agreement_predecessor AS MATERIALIZED");
        sql.Should().Contain("FOR UPDATE");
        sql.Should().Contain("renewal_addendum_validation AS MATERIALIZED");
        sql.Should().Contain("target_addendum_reissue_decision AS MATERIALIZED");
        sql.Should().Contain("target_addendum_renewal_reissue AS MATERIALIZED");
        sql.Should().Contain("EXCEPT SELECT \"SourceAddendumSeriesPublicId\"");
        sql.Should().Contain("replacement.\"FullyExecutedAtUtc\" IS NULL");
        sql.Should().Contain("replacement.\"ExecutedArtifactId\" IS NULL");
        sql.Should().Contain("replacement.\"ReplacesAddendumId\" IS DISTINCT FROM source.\"Id\"");
        sql.Should().Contain("replacement.\"SupersededEffectiveOn\" IS NOT NULL");
        sql.Should().Contain("AND NOT EXISTS (SELECT 1 FROM target_addendum_reissue_decision)");
        sql.Should().Contain("AND NOT EXISTS (SELECT 1 FROM target_addendum_renewal_reissue)");
        sql.Should().NotContain("newer.\"VersionNumber\" > source.\"VersionNumber\"");
        sql.Should().Contain("UPDATE \"LeaseAgreements\"");
        sql.Should().Contain("UPDATE \"LeaseAddenda\"");
        sql.Should().Contain("'ReissueAsAddendum'");
        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDmlTargets(sql).Should().BeEquivalentTo(
        [
            new AtomicRawDmlTarget("LeaseAgreements", AtomicRawDmlOperation.Update),
            new AtomicRawDmlTarget("LeaseAddenda", AtomicRawDmlOperation.Update),
        ]);
    }

    [Fact]
    public void Renewal_reissue_issue_eligibility_is_one_server_side_query()
    {
        using var db = Context();
        var query = db.LeaseAddenda
            .Where(item => item.Id == 31 && item.PortfolioId == 7 && item.LeaseManagementId == 19)
            .Select(item => new
            {
                Eligible = item.BaseAgreement != null
                    && item.BaseAgreement.VoidedAtUtc == null
                    && item.BaseAgreement.DraftCanceledAtUtc == null
                    && ((item.BaseAgreement.FullyExecutedAtUtc != null
                            && item.BaseAgreement.ExecutedArtifactId != null)
                        || (item.BaseAgreement.FullyExecutedAtUtc == null
                            && item.BaseAgreement.ExecutedArtifactId == null
                            && item.BaseAgreement.IssuedAtUtc != null
                            && item.BaseAgreement.IssuedArtifactId != null
                            && (item.BaseAgreement.ChangeType == LeaseAgreementChangeType.Renewal
                                || item.BaseAgreement.ChangeType == LeaseAgreementChangeType.MonthToMonth)
                            && item.ReplacesAddendum != null
                            && item.ReplacesAddendum.FullyExecutedAtUtc != null
                            && item.ReplacesAddendum.ExecutedArtifactId != null
                            && item.ReplacesAddendum.VoidedAtUtc == null
                            && item.ReplacesAddendum.DraftCanceledAtUtc == null
                            && item.ReplacesAddendum.SupersededEffectiveOn == null
                            && item.ReplacesAddendum.SupersededByAddendumId == null
                            && item.ReplacesAddendum.SeriesPublicId == item.SeriesPublicId
                            && item.ReplacesAddendum.BaseAgreementId == item.BaseAgreement.RenewsAgreementId
                            && item.ReplacesAddendum.EffectiveFromOn < item.BaseAgreement.GoverningFromOn
                            && (item.ReplacesAddendum.EffectiveThroughOn == null
                                || item.ReplacesAddendum.EffectiveThroughOn >= item.BaseAgreement.GoverningFromOn)
                            && item.EffectiveFromOn == item.BaseAgreement.GoverningFromOn
                            && item.BaseAgreement.RenewalAddendumDecisions.Any(decision =>
                                decision.Decision == LeaseRenewalAddendumDecisionType.ReissueAsAddendum
                                && decision.ReplacementAddendumId == item.Id
                                && decision.SourceAddendumSeriesPublicId == item.SeriesPublicId))),
            });

        var sql = query.ToQueryString();
        sql.Should().Contain("AS \"Eligible\"");
        sql.Should().Contain("EXISTS (");
        sql.Should().Contain("\"LeaseRenewalAddendumDecisions\"");
        sql.Should().Contain("'ReissueAsAddendum'");
        sql.Should().Contain("\"IssuedArtifactId\" IS NOT NULL");
        sql.Should().Contain("\"ExecutedArtifactId\" IS NOT NULL");
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
