using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class SuppliedLegalNoticeTemplateV3MigrationTests : IAsyncLifetime
{
    private const string PreV3Migration =
        "20260724150000_AddEffectiveCapabilityScopeAuthority";
    private const string V3Migration =
        "20260725090000_AddSafeSuppliedLegalNoticeTemplateV3";

    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_v3_migration_host")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Migration_UpgradesOnlyExactUnreviewedDraftV2Bindings_AndPreservesHistory()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; supplied legal v3 migration proof skipped.");

        var connectionString = await CreatePreV3DatabaseAsync();
        var now = new DateTime(2026, 7, 25, 8, 0, 0, DateTimeKind.Utc);
        Variant qualifyingNonRenewal;
        Variant qualifyingLateRent;
        Variant customized;
        Variant workspaceReviewed;
        Variant policyReviewed;
        Variant automatic;
        Variant disabled;
        Variant unboundHistorical;
        int draftId;
        long renderedId;

        await using (var seed = NewContext(connectionString))
        {
            var actor = Actor(now);
            seed.Users.Add(actor);
            await seed.SaveChangesAsync();

            qualifyingNonRenewal = await SeedVariantAsync(
                seed, actor.Id, "qualifying-non-renewal", "lease-non-renewal", now);
            qualifyingLateRent = await SeedVariantAsync(
                seed, actor.Id, "qualifying-late-rent", "late-rent-late-fee", now);
            customized = await SeedVariantAsync(
                seed, actor.Id, "customized", "lease-non-renewal", now, isCustomized: true);
            workspaceReviewed = await SeedVariantAsync(
                seed, actor.Id, "workspace-reviewed", "late-rent-late-fee", now,
                workspaceReviewed: true);
            policyReviewed = await SeedVariantAsync(
                seed, actor.Id, "policy-reviewed", "lease-non-renewal", now,
                policyReviewed: true);
            automatic = await SeedVariantAsync(
                seed, actor.Id, "automatic", "late-rent-late-fee", now,
                mode: TenantNoticeMode.Auto);
            disabled = await SeedVariantAsync(
                seed, actor.Id, "disabled", "lease-non-renewal", now,
                mode: TenantNoticeMode.Off);
            unboundHistorical = await SeedVariantAsync(
                seed, actor.Id, "unbound-history", "late-rent-late-fee", now,
                bindPolicy: false);

            var history = await SeedFrozenHistoryAsync(
                seed, actor.Id, qualifyingNonRenewal, now);
            draftId = history.DraftId;
            renderedId = history.RenderedId;
        }

        await using (var migrate = NewContext(connectionString))
        {
            await migrate.GetService<IMigrator>().MigrateAsync(V3Migration);
        }

        await using var verify = NewContext(connectionString);
        var v3System = await verify.SystemNoticeTemplateVersions.AsNoTracking()
            .Where(template => template.Version == 3)
            .OrderBy(template => template.Id)
            .ToListAsync();
        v3System.Select(template => template.Id).Should().Equal(8, 9);
        v3System.Select(template => template.Body)
            .Should().Equal(SuppliedNoticeTemplateBaseline.V3Legal.Select(template => template.Body));

        var selected = new[] { qualifyingNonRenewal, qualifyingLateRent };
        foreach (var variant in selected)
        {
            var policy = await verify.TenantNoticePolicies.AsNoTracking()
                .SingleAsync(row => row.Id == variant.PolicyId);
            policy.WorkspaceNoticeTemplateVersionId.Should().NotBe(variant.WorkspaceTemplateId);
            var workspace = await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
                .SingleAsync(row => row.Id == policy.WorkspaceNoticeTemplateVersionId);
            var expected = SuppliedNoticeTemplateBaseline.V3Legal.Single(template =>
                template.SystemKey == variant.SystemKey);
            workspace.PortfolioId.Should().Be(variant.PortfolioId);
            workspace.SystemKey.Should().Be(variant.SystemKey);
            workspace.Version.Should().Be(3);
            workspace.BasedOnSystemTemplateVersionId.Should().Be(expected.Id);
            workspace.IsCustomized.Should().BeFalse();
            workspace.Subject.Should().Be(expected.Subject);
            workspace.Body.Should().Be(expected.Body);
            workspace.JurisdictionReviewedAtUtc.Should().BeNull();
        }

        foreach (var variant in new[]
                 {
                     customized, workspaceReviewed, policyReviewed, automatic, disabled,
                 })
        {
            (await verify.TenantNoticePolicies.AsNoTracking()
                    .Where(policy => policy.Id == variant.PolicyId)
                    .Select(policy => policy.WorkspaceNoticeTemplateVersionId)
                    .SingleAsync())
                .Should().Be(variant.WorkspaceTemplateId);
        }

        (await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
                .AnyAsync(template => template.Id == unboundHistorical.WorkspaceTemplateId))
            .Should().BeTrue();
        (await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
                .CountAsync(template => template.Version == 3))
            .Should().Be(2);

        var frozenV2 = SuppliedNoticeTemplateBaseline.V2Legal[0];
        var draft = await verify.NoticeDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
        draft.WorkspaceNoticeTemplateVersionId.Should().Be(
            qualifyingNonRenewal.WorkspaceTemplateId);
        draft.Subject.Should().Be(frozenV2.Subject);
        draft.Body.Should().Be(frozenV2.Body);
        var rendered = await verify.RenderedNotices.AsNoTracking()
            .SingleAsync(row => row.Id == renderedId);
        rendered.WorkspaceNoticeTemplateVersionId.Should().Be(
            qualifyingNonRenewal.WorkspaceTemplateId);
        rendered.Subject.Should().Be(frozenV2.Subject);
        rendered.Body.Should().Be(frozenV2.Body);
        rendered.TemplateProvenance.Should().Be(
            SuppliedNoticeTemplateBaseline.LegalV2Provenance);

        var unchangedV2 = await verify.SystemNoticeTemplateVersions.AsNoTracking()
            .Where(template => template.Id == 6 || template.Id == 7)
            .OrderBy(template => template.Id)
            .Select(template => new { template.Id, template.Subject, template.Body })
            .ToListAsync();
        unchangedV2.Select(template => template.Id).Should().Equal(6, 7);
        unchangedV2.Select(template => template.Subject)
            .Should().Equal(SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.Subject));
        unchangedV2.Select(template => template.Body)
            .Should().Equal(SuppliedNoticeTemplateBaseline.V2Legal.Select(template => template.Body));
    }

    [SkippableFact]
    public async Task Migration_FailureRollsBackSystemWorkspaceAndPolicyChanges()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; supplied legal v3 rollback proof skipped.");

        var connectionString = await CreatePreV3DatabaseAsync();
        var now = new DateTime(2026, 7, 25, 8, 30, 0, DateTimeKind.Utc);
        Variant firstCandidate;
        Variant conflictingCandidate;
        int conflictWorkspaceId;

        await using (var seed = NewContext(connectionString))
        {
            var actor = Actor(now);
            seed.Users.Add(actor);
            await seed.SaveChangesAsync();
            firstCandidate = await SeedVariantAsync(
                seed, actor.Id, "rollback-first", "lease-non-renewal", now);
            conflictingCandidate = await SeedVariantAsync(
                seed, actor.Id, "rollback-conflict", "late-rent-late-fee", now);

            var v2 = SuppliedNoticeTemplateBaseline.V2Legal.Single(template =>
                template.SystemKey == conflictingCandidate.SystemKey);
            var conflict = new WorkspaceNoticeTemplateVersion
            {
                PortfolioId = conflictingCandidate.PortfolioId,
                SystemKey = conflictingCandidate.SystemKey,
                Version = 3,
                BasedOnSystemTemplateVersionId = v2.Id,
                IsCustomized = true,
                Subject = "Pre-existing conflicting v3",
                Body = "This row forces the migration insert to fail.",
                CreatedByUserId = actor.Id,
                CreatedAtUtc = now,
            };
            seed.WorkspaceNoticeTemplateVersions.Add(conflict);
            await seed.SaveChangesAsync();
            conflictWorkspaceId = conflict.Id;
        }

        await using (var migrate = NewContext(connectionString))
        {
            var act = () => migrate.GetService<IMigrator>().MigrateAsync(V3Migration);
            await act.Should().ThrowAsync<Exception>();
        }

        await using var verify = NewContext(connectionString);
        (await verify.SystemNoticeTemplateVersions.AsNoTracking()
                .CountAsync(template => template.Id == 8 || template.Id == 9))
            .Should().Be(0);
        var v3WorkspaceIds = await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            .Where(template => template.Version == 3)
            .Select(template => template.Id)
            .ToListAsync();
        v3WorkspaceIds.Should().Equal(conflictWorkspaceId);
        (await verify.TenantNoticePolicies.AsNoTracking()
                .Where(policy => policy.Id == firstCandidate.PolicyId)
                .Select(policy => policy.WorkspaceNoticeTemplateVersionId)
                .SingleAsync())
            .Should().Be(firstCandidate.WorkspaceTemplateId);
        (await verify.TenantNoticePolicies.AsNoTracking()
                .Where(policy => policy.Id == conflictingCandidate.PolicyId)
                .Select(policy => policy.WorkspaceNoticeTemplateVersionId)
                .SingleAsync())
            .Should().Be(conflictingCandidate.WorkspaceTemplateId);
        (await verify.Database.GetAppliedMigrationsAsync()).Should().NotContain(V3Migration);
    }

    private async Task<string> CreatePreV3DatabaseAsync()
    {
        var database = $"rentalcommand_v3_{Guid.NewGuid():N}";
        var hostConnection = new NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = "postgres",
        };
        await using (var connection = new NpgsqlConnection(hostConnection.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var databaseConnection = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;
        await using var db = NewContext(databaseConnection);
        await db.GetService<IMigrator>().MigrateAsync(PreV3Migration);
        return databaseConnection;
    }

    private static RentalCommandDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }

    private static ApplicationUser Actor(DateTime now) => new()
    {
        UserName = $"v3-migration-{Guid.NewGuid():N}@example.test",
        NormalizedUserName = $"V3-MIGRATION-{Guid.NewGuid():N}@EXAMPLE.TEST",
        Email = $"v3-migration-{Guid.NewGuid():N}@example.test",
        NormalizedEmail = $"V3-MIGRATION-{Guid.NewGuid():N}@EXAMPLE.TEST",
        DisplayName = "V3 Migration Actor",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = now,
    };

    private static async Task<Variant> SeedVariantAsync(
        RentalCommandDbContext db,
        int actorUserId,
        string label,
        string systemKey,
        DateTime now,
        bool isCustomized = false,
        bool workspaceReviewed = false,
        bool policyReviewed = false,
        TenantNoticeMode mode = TenantNoticeMode.Draft,
        bool bindPolicy = true)
    {
        var supplied = SuppliedNoticeTemplateBaseline.V2Legal.Single(template =>
            template.SystemKey == systemKey);
        var portfolio = new Portfolio
        {
            Name = $"V3 {label}",
            ManagementCompanyName = $"V3 {label}",
            Status = PortfolioStatus.Active,
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();

        var workspace = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = portfolio.Id,
            SystemKey = systemKey,
            Version = 2,
            BasedOnSystemTemplateVersionId = supplied.Id,
            IsCustomized = isCustomized,
            Subject = isCustomized ? $"{supplied.Subject} customized" : supplied.Subject,
            Body = isCustomized ? $"{supplied.Body}\nCustomized." : supplied.Body,
            JurisdictionCode = workspaceReviewed ? "OH" : null,
            JurisdictionReviewedAtUtc = workspaceReviewed ? now : null,
            JurisdictionReviewedByUserId = workspaceReviewed ? actorUserId : null,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = now,
        };
        db.WorkspaceNoticeTemplateVersions.Add(workspace);
        await db.SaveChangesAsync();

        if (!bindPolicy)
        {
            return new Variant(portfolio.Id, workspace.Id, 0, systemKey);
        }

        var policy = new TenantNoticePolicy
        {
            PortfolioId = portfolio.Id,
            AutomationKey = systemKey,
            Mode = mode,
            Classification = NoticeClassification.Legal,
            LeadDays = 60,
            SendHourLocal = 9,
            SendTenantPortal = true,
            SendEmail = true,
            IncludePrimaryTenant = true,
            IncludeCoTenant = true,
            FailureBehavior = NoticeFailureBehavior.StopAndRequireReview,
            WorkspaceNoticeTemplateVersionId = workspace.Id,
            ReviewedJurisdictionCode = policyReviewed ? "OH" : null,
            JurisdictionReviewedAtUtc = policyReviewed ? now : null,
            JurisdictionReviewedByUserId = policyReviewed ? actorUserId : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.TenantNoticePolicies.Add(policy);
        await db.SaveChangesAsync();
        return new Variant(portfolio.Id, workspace.Id, policy.Id, systemKey);
    }

    private static async Task<(int DraftId, long RenderedId)> SeedFrozenHistoryAsync(
        RentalCommandDbContext db,
        int actorUserId,
        Variant variant,
        DateTime now)
    {
        var property = new Property
        {
            PortfolioId = variant.PortfolioId,
            Name = "V3 history property",
            AddressLine1 = "8 History Way",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = variant.PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = variant.PortfolioId,
            FirstName = "Frozen",
            LastName = "History",
            Email = "frozen-history@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(unit, tenant);
        await db.SaveChangesAsync();
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = variant.PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-V3-HISTORY",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            RowVersion = Guid.NewGuid(),
        };
        db.LeaseManagements.Add(management);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = variant.PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = "TA-V3-HISTORY",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = variant.PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now),
            ChangeReason = "V3 migration history proof",
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId,
        };
        db.AddRange(account, party);
        await db.SaveChangesAsync();

        var supplied = SuppliedNoticeTemplateBaseline.V2Legal.Single(template =>
            template.SystemKey == variant.SystemKey);
        var draft = new NoticeDraft
        {
            PortfolioId = variant.PortfolioId,
            LeaseManagementId = management.Id,
            TenantAccountId = account.Id,
            RecipientLeaseManagementPartyId = party.Id,
            PropertyId = property.Id,
            NoticeType = variant.SystemKey,
            Status = "Draft",
            Subject = supplied.Subject,
            Body = supplied.Body,
            Reason = "Frozen v2 migration history",
            TriggerDate = now,
            TenantNoticePolicyId = variant.PolicyId,
            WorkspaceNoticeTemplateVersionId = variant.WorkspaceTemplateId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.NoticeDrafts.Add(draft);
        await db.SaveChangesAsync();
        var rendered = new RenderedNotice
        {
            PortfolioId = variant.PortfolioId,
            NoticeDraftId = draft.Id,
            WorkspaceNoticeTemplateVersionId = variant.WorkspaceTemplateId,
            LeaseManagementId = management.Id,
            Subject = supplied.Subject,
            Body = supplied.Body,
            ContentSha256 = new string('a', 64),
            TemplateProvenance = SuppliedNoticeTemplateBaseline.LegalV2Provenance,
            RenderedAtUtc = now,
        };
        db.RenderedNotices.Add(rendered);
        await db.SaveChangesAsync();
        return (draft.Id, rendered.Id);
    }

    private sealed record Variant(
        int PortfolioId,
        int WorkspaceTemplateId,
        int PolicyId,
        string SystemKey);
}
