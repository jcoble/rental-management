using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL-only proof for the set-based tenant-notice draft command.</summary>
public sealed class TenantNoticeDraftSetStorePostgreSqlTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_notice_draft_sets")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(LeaseAgreementStatusViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(LeaseManagementLifecycleViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ClaimedBatch_IsOneSetCommand_AndConcurrentReplayReturnsOnlyExactLedgerDraft()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice-draft proof skipped.");

        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var token = Guid.NewGuid();
        long validWorkId;
        long mismatchedWorkId;
        long validLedgerId;
        long otherLedgerId;
        int validLeaseManagementId;
        int validTenantId;
        int portfolioId;

        await using (var setup = NewContext())
        {
            var actor = new ApplicationUser
            {
                UserName = "notice-set-owner@example.test",
                NormalizedUserName = "NOTICE-SET-OWNER@EXAMPLE.TEST",
                Email = "notice-set-owner@example.test",
                NormalizedEmail = "NOTICE-SET-OWNER@EXAMPLE.TEST",
                DisplayName = "Notice Set Owner",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Notice Set Workspace",
                ManagementCompanyName = "Notice Set Workspace",
                TimeZone = "UTC",
                Currency = "USD",
                Status = PortfolioStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.AddRange(actor, portfolio);
            await setup.SaveChangesAsync();
            setup.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = now,
                RealAnchorUtc = now,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = now,
            });
            await setup.SaveChangesAsync();

            var first = await SeedRelationshipAsync(setup, portfolio, actor, "A", now);
            var second = await SeedRelationshipAsync(setup, portfolio, actor, "B", now);
            var foundation = new NotificationFoundationService(setup, TimeProvider.System);
            await foundation.SeedSuppliedTemplatesAsync(portfolio.Id, actor.Id, CancellationToken.None);
            var policy = await setup.TenantNoticePolicies.SingleAsync(row =>
                row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            policy.Mode = TenantNoticeMode.Draft;

            var valid = ClaimedWork(
                portfolio.Id, policy.Id, first.LeaseManagementId, first.LedgerEntryId, token,
                "notice-set-valid", now);
            var mismatched = ClaimedWork(
                portfolio.Id, policy.Id, first.LeaseManagementId, second.LedgerEntryId, token,
                "notice-set-cross-account", now);
            setup.AddRange(valid, mismatched);
            await setup.SaveChangesAsync();

            portfolioId = portfolio.Id;
            validWorkId = valid.Id;
            mismatchedWorkId = mismatched.Id;
            validLedgerId = first.LedgerEntryId;
            otherLedgerId = second.LedgerEntryId;
            validLeaseManagementId = first.LeaseManagementId;
            validTenantId = first.TenantId;
        }

        var recorder = new CommandRecorder();
        var firstCall = Task.Run(async () =>
        {
            await using var db = NewContext(recorder);
            return await new TenantNoticeDraftSetStore(db).GenerateClaimedBatchAsync(token);
        });
        var secondCall = Task.Run(async () =>
        {
            await using var db = NewContext();
            return await new TenantNoticeDraftSetStore(db).GenerateClaimedBatchAsync(token);
        });
        var concurrent = await Task.WhenAll(firstCall, secondCall);

        concurrent.Should().OnlyContain(rows => rows.Count == 1);
        concurrent.SelectMany(rows => rows).Should().OnlyContain(row =>
            row.WorkItemId == validWorkId && row.TenantLedgerEntryId == validLedgerId);
        concurrent.SelectMany(rows => rows).Count(row => row.WasCreated).Should().Be(1);
        concurrent.SelectMany(rows => rows).Select(row => row.DraftId).Distinct().Should().ContainSingle(
            because: "both concurrent callers must resolve the same canonical draft id");
        concurrent.SelectMany(rows => rows).Should().NotContain(row => row.WorkItemId == mismatchedWorkId);

        recorder.ReaderCommands.Should().ContainSingle();
        var sql = recorder.ReaderCommands.Single();
        sql.Should().Contain("INSERT INTO \"NoticeDrafts\"");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_tenant_charge_balances");
        sql.Should().Contain("WorkspaceNoticeTemplateVersions");
        sql.Should().Contain("row_number() OVER");
        sql.Should().Contain("ON CONFLICT");

        await using (var replay = NewContext())
        {
            var store = new TenantNoticeDraftSetStore(replay);
            var rows = await store.GenerateClaimedBatchAsync(token);
            rows.Should().ContainSingle();
            rows[0].WasCreated.Should().BeFalse();
            rows[0].DraftId.Should().Be(concurrent[0][0].DraftId);

            var manual = await store.GenerateManualAsync(
                portfolioId, null, validLeaseManagementId, null, validLedgerId, "rent-reminder");
            manual.Should().ContainSingle();
            manual[0].WorkItemId.Should().BeNull();
            manual[0].WasCreated.Should().BeFalse();
            manual[0].DraftId.Should().Be(rows[0].DraftId);

            var crossAccount = await store.GenerateManualAsync(
                portfolioId, null, validLeaseManagementId, null, otherLedgerId, "rent-reminder");
            crossAccount.Should().BeEmpty();

            var forcedLifecycle = await store.GenerateManualAsync(
                portfolioId, validTenantId, null, null, null, "lease-renewal-offer");
            forcedLifecycle.Should().ContainSingle(
                because: "an explicitly requested lifecycle notice is valid outside its lead window when tenant-scoped");
            forcedLifecycle[0].WorkItemId.Should().BeNull();
            forcedLifecycle[0].NoticeType.Should().Be("lease-renewal-offer");
        }

        await using var verify = NewContext();
        (await verify.NoticeDrafts.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(2);
        var persisted = await verify.NoticeDrafts.SingleAsync(row =>
            row.PortfolioId == portfolioId && row.NoticeType == "rent-reminder");
        persisted.TenantLedgerEntryId.Should().Be(validLedgerId);
        persisted.Subject.Should().Contain("Upcoming rent reminder");
        persisted.Body.Should().Contain("Resident");
        persisted.Body.Should().NotContain("Casey A",
            because: "an automated draft can be delivered to several eligible parties and must not name only the first one");
        persisted.Body.Should().Contain("$1,000");
    }

    private static TenantNoticeWorkItem ClaimedWork(
        int portfolioId,
        int policyId,
        int leaseManagementId,
        long ledgerEntryId,
        Guid token,
        string businessKey,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        TenantNoticePolicyId = policyId,
        LeaseManagementId = leaseManagementId,
        TenantLedgerEntryId = ledgerEntryId,
        DueAtUtc = now,
        Status = TenantNoticeWorkStatus.Claimed,
        BusinessKey = businessKey,
        AttemptCount = 1,
        ClaimOwner = "notice-set-test",
        ClaimToken = token,
        ClaimExpiresAtUtc = now.AddMinutes(5),
        CreatedAtUtc = now,
    };

    private static async Task<RelationshipSeed> SeedRelationshipAsync(
        RentalCommandDbContext db,
        Portfolio portfolio,
        ApplicationUser actor,
        string suffix,
        DateTime now)
    {
        var property = new Property
        {
            PortfolioId = portfolio.Id,
            Name = $"Notice House {suffix}",
            AddressLine1 = $"{suffix} Notice Way",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(property);
        await db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitNumber = suffix,
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1_000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "Casey",
            LastName = suffix,
            Email = $"casey-{suffix.ToLowerInvariant()}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = portfolio.Id,
            Name = $"Notice Lease {suffix}",
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var issuedFile = StoredFile(portfolio.Id, $"notice-{suffix}-issued.pdf", now);
        var executedFile = StoredFile(portfolio.Id, $"notice-{suffix}-executed.pdf", now);
        db.AddRange(unit, tenant, template, issuedFile, executedFile);
        await db.SaveChangesAsync();
        var source = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
            BusinessKey = $"notice-set-source:{suffix}",
            DocumentTemplateId = template.Id,
            DocumentTemplateVersion = 1,
            RendererKey = "lease-agreement-overlay",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        db.Add(source);
        await db.SaveChangesAsync();
        var issued = LegalArtifact(
            portfolio.Id, actor.Id, issuedFile.Id, LegalDocumentArtifactKind.IssuedAgreement,
            $"notice-{suffix}-issued", now);
        var executed = LegalArtifact(
            portfolio.Id, actor.Id, executedFile.Id, LegalDocumentArtifactKind.ExecutedAgreement,
            $"notice-{suffix}-executed", now);
        db.AddRange(issued, executed);
        await db.SaveChangesAsync();
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-NOTICE-SET-{suffix}",
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        db.Add(management);
        await db.SaveChangesAsync();
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-NOTICE-SET-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-NOTICE-SET-{suffix}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = new DateOnly(2026, 6, 1),
            TermEndOn = new DateOnly(2027, 5, 31),
            GoverningFromOn = new DateOnly(2026, 6, 1),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = source.Id,
            IssuedArtifactId = issued.Id,
            IssuedAtUtc = now.AddMonths(-2),
            ExecutedArtifactId = executed.Id,
            FullyExecutedAtUtc = now.AddMonths(-2).AddDays(1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2026, 6, 1),
            ChangeReason = "Notice set test",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        db.AddRange(account, agreement, party);
        await db.SaveChangesAsync();
        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolio.Id,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1_000m,
            Currency = "USD",
            EffectiveOn = new DateOnly(2026, 7, 1),
            DueOn = new DateOnly(2026, 7, 15),
            PostedAtUtc = now,
            Description = "July rent",
            BusinessKey = $"notice-set-rent:{suffix}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = actor.Id,
        };
        db.Add(charge);
        await db.SaveChangesAsync();
        return new RelationshipSeed(management.Id, tenant.Id, charge.Id);
    }

    private RentalCommandDbContext NewContext(CommandRecorder? recorder = null)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString);
        if (recorder is not null) options.AddInterceptors(recorder);
        return new RentalCommandDbContext(options.Options);
    }

    private static StoredFile StoredFile(int portfolioId, string fileName, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        FileName = fileName,
        FilePath = $"tests/{fileName}",
        ContentType = "application/pdf",
        FileSize = 100,
        UploadedAt = now,
    };

    private static LegalDocumentArtifact LegalArtifact(
        int portfolioId,
        int actorUserId,
        int storedFileId,
        LegalDocumentArtifactKind kind,
        string key,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = portfolioId,
        StoredFileId = storedFileId,
        ArtifactKind = kind,
        StorageKey = $"tests/{key}",
        FileName = $"{key}.pdf",
        ContentType = "application/pdf",
        ByteLength = 100,
        ContentSha256 = new string(kind == LegalDocumentArtifactKind.IssuedAgreement ? 'a' : 'b', 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
            ? new string('c', 64)
            : null,
        CreatedAtUtc = now,
        CreatedByUserId = actorUserId,
    };

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> ReaderCommands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record RelationshipSeed(int LeaseManagementId, int TenantId, long LedgerEntryId);
}
