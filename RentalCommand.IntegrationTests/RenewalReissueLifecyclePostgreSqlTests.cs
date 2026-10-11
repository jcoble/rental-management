using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof that an Addendum reissued for a renewal is executed in two coherent stages:
/// its immutable executed artifact may finish first, while renewal execution alone owns the
/// source cutoff and replacement activation boundary.
/// </summary>
public sealed class RenewalReissueLifecyclePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BoundaryUtc =
        new(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly BoundaryOn = new(2027, 1, 1);

    private SharedPostgreSqlDatabase? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var clock = await db.SimulationClocks.SingleAsync(item => item.Id == 1);
        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = BoundaryUtc;
        clock.RealAnchorUtc = BoundaryUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = BoundaryUtc;
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Replacement_executes_first_without_cutoff_then_renewal_atomically_activates_it()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("ordered");
        var initial = await ReadSnapshotAsync(scenario);
        initial.SourceAgreementExecutedArtifactId.Should().NotBeNull();
        initial.SourceAgreementFullyExecutedAtUtc.Should().NotBeNull();
        initial.SourceAddendumExecutedArtifactId.Should().NotBeNull();
        initial.SourceAddendumFullyExecutedAtUtc.Should().NotBeNull();
        initial.RenewalIssuedArtifactId.Should().NotBeNull();
        initial.RenewalExecutedArtifactId.Should().BeNull();
        initial.ReplacementIssuedArtifactId.Should().NotBeNull();
        initial.ReplacementExecutedArtifactId.Should().BeNull();
        initial.SourceStatus.Should().Be("Active");
        initial.SourceBillable.Should().BeTrue();
        initial.ActiveBillableAddendumCount.Should().Be(1);

        var replacementTransition = await ExecuteTransitionAsync(
            scenario,
            leaseAgreementId: null,
            leaseAddendumId: scenario.ReplacementAddendumId,
            executedArtifactId: scenario.ReplacementExecutedArtifactId,
            executedAtUtc: BoundaryUtc.AddMinutes(-2));

        replacementTransition.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        replacementTransition.SupersededAddendumIds.Should().BeEmpty(
            "a renewal reissue is staged until its base renewal executes");

        var staged = await ReadSnapshotAsync(scenario);
        staged.ReplacementExecutedArtifactId.Should().Be(scenario.ReplacementExecutedArtifactId);
        staged.ReplacementFullyExecutedAtUtc.Should().Be(BoundaryUtc.AddMinutes(-2));
        staged.RenewalExecutedArtifactId.Should().BeNull();
        staged.SourceAgreementSupersededEffectiveOn.Should().BeNull();
        staged.SourceAgreementSupersededByAgreementId.Should().BeNull();
        staged.SourceSupersededEffectiveOn.Should().BeNull();
        staged.SourceSupersededByAddendumId.Should().BeNull();
        staged.SourceStatus.Should().Be("Active");
        staged.SourceBillable.Should().BeTrue();
        staged.ReplacementBillable.Should().BeFalse();
        staged.IgnoredHigherDraftCanceledAtUtc.Should().NotBeNull();
        staged.LegallyEffectiveSeriesVersionCount.Should().Be(1,
            "neither a canceled version nor a staged replacement with an unexecuted base can hide the governing predecessor");
        staged.ActiveBillableAddendumCount.Should().Be(1,
            "the governing source remains the sole billable series version before renewal execution");

        var renewalTransition = await ExecuteTransitionAsync(
            scenario,
            leaseAgreementId: scenario.RenewalAgreementId,
            leaseAddendumId: null,
            executedArtifactId: scenario.RenewalExecutedArtifactId,
            executedAtUtc: BoundaryUtc);

        renewalTransition.Outcome.Should().Be(AtomicLegalExecutionTransitionOutcome.Applied);
        renewalTransition.SupersededAddendumIds.Should().Equal(scenario.SourceAddendumId);
        renewalTransition.ReissuedAddendumIds.Should().Equal(scenario.ReplacementAddendumId);

        var activated = await ReadSnapshotAsync(scenario);
        activated.RenewalExecutedArtifactId.Should().Be(scenario.RenewalExecutedArtifactId);
        activated.RenewalFullyExecutedAtUtc.Should().Be(BoundaryUtc);
        activated.SourceAgreementSupersededEffectiveOn.Should().Be(BoundaryOn);
        activated.SourceAgreementSupersededByAgreementId.Should().Be(scenario.RenewalAgreementId);
        activated.SourceSupersededEffectiveOn.Should().Be(BoundaryOn);
        activated.SourceSupersededByAddendumId.Should().Be(scenario.ReplacementAddendumId);
        activated.SourceStatus.Should().Be("Superseded");
        activated.SourceBillable.Should().BeFalse();
        activated.ReplacementStatus.Should().Be("Active");
        activated.ReplacementBillable.Should().BeTrue();
        activated.LegallyEffectiveSeriesVersionCount.Should().Be(1,
            "the executed replacement becomes the sole legally effective version with its renewal");
        activated.ActiveBillableAddendumCount.Should().Be(1,
            "the canonical status/billing view switches directly from source to replacement");
    }

    [SkippableFact]
    public async Task Renewal_rejects_unexecuted_replacement_without_mutating_any_legal_row()
    {
        SkipIfNoDocker();
        var scenario = await SeedScenarioAsync("blocked");
        var before = await ReadSnapshotAsync(scenario);

        var renewalTransition = await ExecuteTransitionAsync(
            scenario,
            leaseAgreementId: scenario.RenewalAgreementId,
            leaseAddendumId: null,
            executedArtifactId: scenario.RenewalExecutedArtifactId,
            executedAtUtc: BoundaryUtc);

        renewalTransition.Outcome.Should().Be(
            AtomicLegalExecutionTransitionOutcome.RenewalAddendumStateChanged);

        var after = await ReadSnapshotAsync(scenario);
        after.Should().BeEquivalentTo(before,
            "the rejected set-based transition must leave renewal, source, and replacement unchanged");
        after.RenewalExecutedArtifactId.Should().BeNull();
        after.ReplacementExecutedArtifactId.Should().BeNull();
        after.SourceSupersededEffectiveOn.Should().BeNull();
        after.SourceSupersededByAddendumId.Should().BeNull();
        after.SourceStatus.Should().Be("Active");
        after.SourceBillable.Should().BeTrue();
        after.IgnoredHigherDraftCanceledAtUtc.Should().NotBeNull();
        after.LegallyEffectiveSeriesVersionCount.Should().Be(1);
        after.ActiveBillableAddendumCount.Should().Be(1);
    }

    private async Task<AtomicLegalExecutionTransitionResult> ExecuteTransitionAsync(
        Scenario scenario,
        int? leaseAgreementId,
        int? leaseAddendumId,
        int executedArtifactId,
        DateTime executedAtUtc)
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var auditScope = new AtomicAuditScope(TimeProvider.System);
        var attemptId = Guid.NewGuid();
        var commandContext = new AtomicCommandContext(db, auditScope, TimeProvider.System);
        commandContext.BeginAttempt(attemptId);
        using var attempt = auditScope.BeginAttempt(
            new AtomicCommandIdentity("test.renewal-reissue-transition", Guid.NewGuid().ToString("N")),
            attemptId,
            db);

        var result = await AtomicLeaseMutationPersistence.ExecuteLegalArtifactTransitionAsync(
            db,
            commandContext,
            scenario.PortfolioId,
            scenario.LeaseManagementId,
            leaseAgreementId,
            leaseAddendumId,
            executedArtifactId,
            executedAtUtc);
        await transaction.CommitAsync();
        commandContext.EndAttempt();
        return result;
    }

    private async Task<LifecycleSnapshot> ReadSnapshotAsync(Scenario scenario)
    {
        await using var db = NewContext();
        return await db.Database.SqlQuery<LifecycleSnapshot>($"""
            SELECT source_agreement."SupersededEffectiveOn" AS "SourceAgreementSupersededEffectiveOn",
                   source_agreement."SupersededByAgreementId" AS "SourceAgreementSupersededByAgreementId",
                   source_agreement."ExecutedArtifactId" AS "SourceAgreementExecutedArtifactId",
                   source_agreement."FullyExecutedAtUtc" AS "SourceAgreementFullyExecutedAtUtc",
                   renewal."IssuedArtifactId" AS "RenewalIssuedArtifactId",
                   renewal."ExecutedArtifactId" AS "RenewalExecutedArtifactId",
                   renewal."FullyExecutedAtUtc" AS "RenewalFullyExecutedAtUtc",
                   replacement."IssuedArtifactId" AS "ReplacementIssuedArtifactId",
                   replacement."ExecutedArtifactId" AS "ReplacementExecutedArtifactId",
                   replacement."FullyExecutedAtUtc" AS "ReplacementFullyExecutedAtUtc",
                   source."ExecutedArtifactId" AS "SourceAddendumExecutedArtifactId",
                   source."FullyExecutedAtUtc" AS "SourceAddendumFullyExecutedAtUtc",
                   source."SupersededEffectiveOn" AS "SourceSupersededEffectiveOn",
                   source."SupersededByAddendumId" AS "SourceSupersededByAddendumId",
                   source_status."AddendumStatus" AS "SourceStatus",
                   source_status."HasCurrentlyBillableFinancialEffect" AS "SourceBillable",
                   replacement_status."AddendumStatus" AS "ReplacementStatus",
                   replacement_status."HasCurrentlyBillableFinancialEffect" AS "ReplacementBillable",
                   ignored_higher."DraftCanceledAtUtc" AS "IgnoredHigherDraftCanceledAtUtc",
                   jsonb_build_object(
                       'sourceAgreement', to_jsonb(source_agreement),
                       'renewalAgreement', to_jsonb(renewal),
                       'sourceAddendum', to_jsonb(source),
                       'ignoredHigherAddendum', to_jsonb(ignored_higher),
                       'replacementAddendum', to_jsonb(replacement))::text AS "LegalRowsSnapshotJson",
                   (
                       SELECT count(*)::integer
                       FROM "LeaseAddenda" AS candidate
                       INNER JOIN "LeaseAgreements" AS candidate_base
                         ON candidate_base."Id" = candidate."BaseAgreementId"
                        AND candidate_base."PortfolioId" = candidate."PortfolioId"
                        AND candidate_base."LeaseManagementId" = candidate."LeaseManagementId"
                        AND candidate_base."FullyExecutedAtUtc" IS NOT NULL
                        AND candidate_base."ExecutedArtifactId" IS NOT NULL
                        AND candidate_base."VoidedAtUtc" IS NULL
                        AND candidate_base."DraftCanceledAtUtc" IS NULL
                       WHERE candidate."PortfolioId" = {scenario.PortfolioId}
                         AND candidate."LeaseManagementId" = {scenario.LeaseManagementId}
                         AND candidate."SeriesPublicId" = source."SeriesPublicId"
                         AND candidate."FullyExecutedAtUtc" IS NOT NULL
                         AND candidate."ExecutedArtifactId" IS NOT NULL
                         AND candidate."VoidedAtUtc" IS NULL
                         AND candidate."DraftCanceledAtUtc" IS NULL
                         AND candidate."EffectiveFromOn" <= rc_business_date({scenario.PortfolioId})
                         AND (candidate."EffectiveThroughOn" IS NULL
                              OR candidate."EffectiveThroughOn" >= rc_business_date({scenario.PortfolioId}))
                         AND (candidate."SupersededEffectiveOn" IS NULL
                              OR candidate."SupersededEffectiveOn" > rc_business_date({scenario.PortfolioId}))
                         AND NOT EXISTS (
                             SELECT 1
                             FROM "LeaseAddenda" AS newer
                             INNER JOIN "LeaseAgreements" AS newer_base
                               ON newer_base."Id" = newer."BaseAgreementId"
                              AND newer_base."PortfolioId" = newer."PortfolioId"
                              AND newer_base."LeaseManagementId" = newer."LeaseManagementId"
                              AND newer_base."FullyExecutedAtUtc" IS NOT NULL
                              AND newer_base."ExecutedArtifactId" IS NOT NULL
                              AND newer_base."VoidedAtUtc" IS NULL
                              AND newer_base."DraftCanceledAtUtc" IS NULL
                             WHERE newer."PortfolioId" = candidate."PortfolioId"
                               AND newer."LeaseManagementId" = candidate."LeaseManagementId"
                               AND newer."SeriesPublicId" = candidate."SeriesPublicId"
                               AND newer."VersionNumber" > candidate."VersionNumber"
                               AND newer."FullyExecutedAtUtc" IS NOT NULL
                               AND newer."ExecutedArtifactId" IS NOT NULL
                               AND newer."VoidedAtUtc" IS NULL
                               AND newer."DraftCanceledAtUtc" IS NULL
                               AND newer."EffectiveFromOn" <= rc_business_date({scenario.PortfolioId})
                               AND (newer."EffectiveThroughOn" IS NULL
                                    OR newer."EffectiveThroughOn" >= rc_business_date({scenario.PortfolioId}))
                               AND (newer."SupersededEffectiveOn" IS NULL
                                    OR newer."SupersededEffectiveOn" > rc_business_date({scenario.PortfolioId})))
                   ) AS "LegallyEffectiveSeriesVersionCount",
                   (
                       SELECT count(*)::integer
                       FROM "vw_lease_addendum_status" AS status
                       INNER JOIN "LeaseAddenda" AS billed
                         ON billed."Id" = status."LeaseAddendumId"
                        AND billed."PortfolioId" = status."PortfolioId"
                        AND billed."LeaseManagementId" = status."LeaseManagementId"
                        AND billed."SeriesPublicId" = source."SeriesPublicId"
                       WHERE status."PortfolioId" = {scenario.PortfolioId}
                         AND status."LeaseManagementId" = {scenario.LeaseManagementId}
                         AND status."AddendumStatus" = 'Active'
                         AND status."HasCurrentlyBillableFinancialEffect"
                   ) AS "ActiveBillableAddendumCount"
            FROM "LeaseAgreements" AS renewal
            INNER JOIN "LeaseAgreements" AS source_agreement
              ON source_agreement."Id" = {scenario.SourceAgreementId}
             AND source_agreement."PortfolioId" = renewal."PortfolioId"
             AND source_agreement."LeaseManagementId" = renewal."LeaseManagementId"
            INNER JOIN "LeaseAddenda" AS source
              ON source."Id" = {scenario.SourceAddendumId}
             AND source."PortfolioId" = renewal."PortfolioId"
             AND source."LeaseManagementId" = renewal."LeaseManagementId"
            INNER JOIN "LeaseAddenda" AS replacement
              ON replacement."Id" = {scenario.ReplacementAddendumId}
             AND replacement."PortfolioId" = renewal."PortfolioId"
             AND replacement."LeaseManagementId" = renewal."LeaseManagementId"
            INNER JOIN "LeaseAddenda" AS ignored_higher
              ON ignored_higher."Id" = {scenario.IgnoredHigherAddendumId}
             AND ignored_higher."PortfolioId" = renewal."PortfolioId"
             AND ignored_higher."LeaseManagementId" = renewal."LeaseManagementId"
            INNER JOIN "vw_lease_addendum_status" AS source_status
              ON source_status."PortfolioId" = source."PortfolioId"
             AND source_status."LeaseManagementId" = source."LeaseManagementId"
             AND source_status."LeaseAddendumId" = source."Id"
            INNER JOIN "vw_lease_addendum_status" AS replacement_status
              ON replacement_status."PortfolioId" = replacement."PortfolioId"
             AND replacement_status."LeaseManagementId" = replacement."LeaseManagementId"
             AND replacement_status."LeaseAddendumId" = replacement."Id"
            WHERE renewal."Id" = {scenario.RenewalAgreementId}
              AND renewal."PortfolioId" = {scenario.PortfolioId}
              AND renewal."LeaseManagementId" = {scenario.LeaseManagementId}
            """).SingleAsync();
    }

    private async Task<Scenario> SeedScenarioAsync(string suffix)
    {
        await using var db = NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var createdAt = BoundaryUtc.AddMonths(-8);
        var actor = new ApplicationUser
        {
            UserName = $"renewal-reissue-{suffix}@example.test",
            NormalizedUserName = $"RENEWAL-REISSUE-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"renewal-reissue-{suffix}@example.test",
            NormalizedEmail = $"RENEWAL-REISSUE-{suffix.ToUpperInvariant()}@EXAMPLE.TEST",
            DisplayName = $"Renewal Reissue {suffix}",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt,
        };
        var portfolio = new Portfolio
        {
            Name = $"Renewal reissue {suffix}",
            ManagementCompanyName = "Lifecycle Test Co",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.AddRange(actor, portfolio);
        await db.SaveChangesAsync();

        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Boundary House {suffix}",
            AddressLine1 = "1 Renewal Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitNumber = suffix,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = $"Renewal template {suffix}",
            Version = 1,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
        };
        db.AddRange(unit, template);
        await db.SaveChangesAsync();
        var sourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"renewal-reissue:{suffix}",
            DocumentTemplateId = template.Id,
            DocumentTemplateVersion = template.Version,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = createdAt,
            CreatedByUserId = actor.Id,
        };
        db.LegalDocumentSourceVersions.Add(sourceVersion);

        var files = Enumerable.Range(1, 8)
            .Select(index => StoredFile(portfolio.Id, suffix, index, createdAt))
            .ToArray();
        db.StoredFiles.AddRange(files);
        await db.SaveChangesAsync();
        var artifacts = new[]
        {
            Artifact(portfolio.Id, actor.Id, files[0], LegalDocumentArtifactKind.IssuedAgreement, '1', createdAt),
            Artifact(portfolio.Id, actor.Id, files[1], LegalDocumentArtifactKind.ExecutedAgreement, '2', createdAt),
            Artifact(portfolio.Id, actor.Id, files[2], LegalDocumentArtifactKind.IssuedAgreement, '3', createdAt),
            Artifact(portfolio.Id, actor.Id, files[3], LegalDocumentArtifactKind.ExecutedAgreement, '4', createdAt),
            Artifact(portfolio.Id, actor.Id, files[4], LegalDocumentArtifactKind.IssuedAddendum, '5', createdAt),
            Artifact(portfolio.Id, actor.Id, files[5], LegalDocumentArtifactKind.ExecutedAddendum, '6', createdAt),
            Artifact(portfolio.Id, actor.Id, files[6], LegalDocumentArtifactKind.IssuedAddendum, '7', createdAt),
            Artifact(portfolio.Id, actor.Id, files[7], LegalDocumentArtifactKind.ExecutedAddendum, '8', createdAt),
        };
        db.LegalDocumentArtifacts.AddRange(artifacts);
        await db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-REISSUE-{suffix}",
            PossessionGivenAtUtc = createdAt,
            CreatedAtUtc = createdAt,
            CreatedByUserId = actor.Id,
            UpdatedAtUtc = createdAt,
            RowVersion = Guid.NewGuid(),
        };
        db.LeaseManagements.Add(management);
        await db.SaveChangesAsync();

        var sourceAgreement = Agreement(
            portfolio.Id, management.Id, actor.Id, sourceVersion.Id,
            1, $"AGR-{suffix}", LeaseAgreementChangeType.Initial,
            null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        db.LeaseAgreements.Add(sourceAgreement);
        await db.SaveChangesAsync();
        var renewal = Agreement(
            portfolio.Id, management.Id, actor.Id, sourceVersion.Id,
            2, $"AGR-{suffix}-R1", LeaseAgreementChangeType.Renewal,
            sourceAgreement.Id, BoundaryOn, new DateOnly(2027, 12, 31));
        db.LeaseAgreements.Add(renewal);
        await db.SaveChangesAsync();

        var seriesId = Guid.NewGuid();
        var sourceAddendum = Addendum(
            portfolio.Id, management.Id, sourceAgreement.Id, actor.Id, sourceVersion.Id,
            seriesId, 1, null, $"ADD-{suffix}", new DateOnly(2026, 6, 1));
        db.LeaseAddenda.Add(sourceAddendum);
        await db.SaveChangesAsync();
        var ignoredHigher = Addendum(
            portfolio.Id, management.Id, sourceAgreement.Id, actor.Id, sourceVersion.Id,
            seriesId, 2, sourceAddendum.Id, $"ADD-{suffix}", new DateOnly(2026, 9, 1));
        ignoredHigher.DraftCanceledAtUtc = BoundaryUtc.AddMonths(-2);
        ignoredHigher.DraftCancellationReason = "Canceled draft must not hide the governing version";
        db.LeaseAddenda.Add(ignoredHigher);
        await db.SaveChangesAsync();
        var replacement = Addendum(
            portfolio.Id, management.Id, renewal.Id, actor.Id, sourceVersion.Id,
            seriesId, 3, sourceAddendum.Id, $"ADD-{suffix}", BoundaryOn);
        db.LeaseAddenda.Add(replacement);
        await db.SaveChangesAsync();

        db.AddRange(
            AgreementSigner(portfolio.Id, sourceAgreement.Id, $"source-{suffix}@example.test"),
            AgreementSigner(portfolio.Id, renewal.Id, $"renewal-{suffix}@example.test"),
            AddendumSigner(portfolio.Id, sourceAddendum.Id, $"source-addendum-{suffix}@example.test"),
            AddendumSigner(portfolio.Id, replacement.Id, $"replacement-{suffix}@example.test"),
            RecurringEffect(portfolio.Id, sourceAddendum.Id, new DateOnly(2026, 6, 1)),
            RecurringEffect(portfolio.Id, replacement.Id, BoundaryOn),
            new LeaseRenewalAddendumDecision
            {
                PortfolioId = portfolio.Id,
                LeaseManagementId = management.Id,
                RenewalAgreementId = renewal.Id,
                SourceAddendumSeriesPublicId = seriesId,
                Decision = LeaseRenewalAddendumDecisionType.ReissueAsAddendum,
                ReplacementAddendumId = replacement.Id,
                CreatedAtUtc = createdAt,
                CreatedByUserId = actor.Id,
            });
        await db.SaveChangesAsync();

        sourceAgreement.IssuedArtifactId = artifacts[0].Id;
        sourceAgreement.IssuedAtUtc = createdAt.AddDays(1);
        sourceAgreement.ExecutedArtifactId = artifacts[1].Id;
        sourceAgreement.FullyExecutedAtUtc = createdAt.AddDays(2);
        renewal.IssuedArtifactId = artifacts[2].Id;
        renewal.IssuedAtUtc = BoundaryUtc.AddDays(-14);
        sourceAddendum.IssuedArtifactId = artifacts[4].Id;
        sourceAddendum.IssuedAtUtc = createdAt.AddMonths(1);
        sourceAddendum.ExecutedArtifactId = artifacts[5].Id;
        sourceAddendum.FullyExecutedAtUtc = createdAt.AddMonths(1).AddDays(1);
        replacement.IssuedArtifactId = artifacts[6].Id;
        replacement.IssuedAtUtc = BoundaryUtc.AddDays(-13);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new(
            portfolio.Id,
            management.Id,
            sourceAgreement.Id,
            sourceAddendum.Id,
            ignoredHigher.Id,
            renewal.Id,
            replacement.Id,
            artifacts[3].Id,
            artifacts[7].Id);
    }

    private static LeaseAgreement Agreement(
        int portfolioId,
        int leaseManagementId,
        int actorUserId,
        int sourceVersionId,
        int version,
        string number,
        LeaseAgreementChangeType changeType,
        int? renewsAgreementId,
        DateOnly startsOn,
        DateOnly endsOn) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = portfolioId,
        LeaseManagementId = leaseManagementId,
        VersionNumber = version,
        AgreementNumber = number,
        ChangeType = changeType,
        RenewsAgreementId = renewsAgreementId,
        TermType = LeaseAgreementTermType.FixedTerm,
        TermStartOn = startsOn,
        TermEndOn = endsOn,
        GoverningFromOn = startsOn,
        BaseRentAmount = 1_500m,
        RentDueDay = 1,
        SecurityDepositObligation = 1_500m,
        LateFeeAmount = 50m,
        GracePeriodDays = 5,
        Currency = "USD",
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersionId = sourceVersionId,
        CreatedAtUtc = BoundaryUtc.AddMonths(-8),
        CreatedByUserId = actorUserId,
        UpdatedAtUtc = BoundaryUtc.AddMonths(-8),
        DraftRevision = 1,
    };

    private static LeaseAddendum Addendum(
        int portfolioId,
        int leaseManagementId,
        int baseAgreementId,
        int actorUserId,
        int sourceVersionId,
        Guid seriesId,
        int version,
        int? replacesAddendumId,
        string number,
        DateOnly effectiveFromOn) => new()
    {
        PublicId = Guid.NewGuid(),
        SeriesPublicId = seriesId,
        PortfolioId = portfolioId,
        LeaseManagementId = leaseManagementId,
        BaseAgreementId = baseAgreementId,
        VersionNumber = version,
        AddendumNumber = number,
        Purpose = LeaseAddendumPurpose.Financial,
        ReplacesAddendumId = replacesAddendumId,
        EffectiveFromOn = effectiveFromOn,
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersionId = sourceVersionId,
        CreatedAtUtc = BoundaryUtc.AddMonths(-8),
        CreatedByUserId = actorUserId,
        UpdatedAtUtc = BoundaryUtc.AddMonths(-8),
        DraftRevision = 1,
    };

    private static LeaseAgreementSigner AgreementSigner(int portfolioId, int agreementId, string email) => new()
    {
        PortfolioId = portfolioId,
        LeaseAgreementId = agreementId,
        SignerRole = LeaseLegalSignerRole.PrimaryTenant,
        NameSnapshot = "Test Tenant",
        EmailSnapshot = email,
        SigningOrder = 1,
        IsRequired = true,
    };

    private static LeaseAddendumSigner AddendumSigner(int portfolioId, int addendumId, string email) => new()
    {
        PortfolioId = portfolioId,
        LeaseAddendumId = addendumId,
        SignerRole = LeaseLegalSignerRole.PrimaryTenant,
        NameSnapshot = "Test Tenant",
        EmailSnapshot = email,
        SigningOrder = 1,
        IsRequired = true,
    };

    private static LeaseAddendumFinancialEffect RecurringEffect(
        int portfolioId, int addendumId, DateOnly effectiveFromOn) => new()
    {
        PortfolioId = portfolioId,
        LeaseAddendumId = addendumId,
        EffectType = LeaseAddendumFinancialEffectType.RecurringRentDelta,
        Amount = 25m,
        Currency = "USD",
        ChargeCode = "RENEWAL-REISSUE",
        EffectiveFromOn = effectiveFromOn,
        Description = "Recurring renewal reissue proof",
    };

    private static StoredFile StoredFile(int portfolioId, string suffix, int index, DateTime createdAt) => new()
    {
        PortfolioId = portfolioId,
        FileName = $"{suffix}-{index}.pdf",
        FilePath = $"tests/renewal-reissue/{suffix}-{index}.pdf",
        ContentType = "application/pdf",
        FileSize = 100,
        UploadedAt = createdAt,
    };

    private static LegalDocumentArtifact Artifact(
        int portfolioId,
        int actorUserId,
        StoredFile file,
        LegalDocumentArtifactKind kind,
        char hashCharacter,
        DateTime createdAt) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = portfolioId,
        StoredFileId = file.Id,
        ArtifactKind = kind,
        StorageKey = file.FilePath,
        FileName = file.FileName,
        ContentType = file.ContentType,
        ByteLength = file.FileSize,
        ContentSha256 = new string(hashCharacter, 64),
        LegalIssuanceFingerprint = kind is LegalDocumentArtifactKind.IssuedAgreement
            or LegalDocumentArtifactKind.IssuedAddendum
            ? new string('a', 64)
            : null,
        CreatedAtUtc = createdAt,
        CreatedByUserId = actorUserId,
    };

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for renewal-reissue PostgreSQL verification.");

    private sealed record Scenario(
        int PortfolioId,
        int LeaseManagementId,
        int SourceAgreementId,
        int SourceAddendumId,
        int IgnoredHigherAddendumId,
        int RenewalAgreementId,
        int ReplacementAddendumId,
        int RenewalExecutedArtifactId,
        int ReplacementExecutedArtifactId);

    private sealed class LifecycleSnapshot
    {
        public DateOnly? SourceAgreementSupersededEffectiveOn { get; set; }
        public int? SourceAgreementSupersededByAgreementId { get; set; }
        public int? SourceAgreementExecutedArtifactId { get; set; }
        public DateTime? SourceAgreementFullyExecutedAtUtc { get; set; }
        public int? RenewalIssuedArtifactId { get; set; }
        public int? RenewalExecutedArtifactId { get; set; }
        public DateTime? RenewalFullyExecutedAtUtc { get; set; }
        public int? ReplacementIssuedArtifactId { get; set; }
        public int? ReplacementExecutedArtifactId { get; set; }
        public DateTime? ReplacementFullyExecutedAtUtc { get; set; }
        public int? SourceAddendumExecutedArtifactId { get; set; }
        public DateTime? SourceAddendumFullyExecutedAtUtc { get; set; }
        public DateOnly? SourceSupersededEffectiveOn { get; set; }
        public int? SourceSupersededByAddendumId { get; set; }
        public string SourceStatus { get; set; } = string.Empty;
        public bool SourceBillable { get; set; }
        public string ReplacementStatus { get; set; } = string.Empty;
        public bool ReplacementBillable { get; set; }
        public DateTime? IgnoredHigherDraftCanceledAtUtc { get; set; }
        public string LegalRowsSnapshotJson { get; set; } = string.Empty;
        public int LegallyEffectiveSeriesVersionCount { get; set; }
        public int ActiveBillableAddendumCount { get; set; }
    }
}
