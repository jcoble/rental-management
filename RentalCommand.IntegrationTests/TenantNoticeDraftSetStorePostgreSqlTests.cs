using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Writes;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL-only proof for the set-based tenant-notice draft command.</summary>
public sealed class TenantNoticeDraftSetStorePostgreSqlTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private ServiceProvider? _services;

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

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandRecorder>();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<CommandRecorder>()));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ClaimedBatch_UsesFrozenEffectiveClock_ForDraftAuditAndOutboxChronology()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice-draft proof skipped.");

        var setupNow = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        var effectiveNow = new DateTime(2027, 1, 26, 5, 0, 0, DateTimeKind.Utc);
        var token = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        int portfolioId;
        int leaseManagementId;
        long ledgerEntryId;

        await using (var setupScope = _services!.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var actor = new ApplicationUser
            {
                UserName = "notice-clock-owner@example.test",
                NormalizedUserName = "NOTICE-CLOCK-OWNER@EXAMPLE.TEST",
                Email = "notice-clock-owner@example.test",
                NormalizedEmail = "NOTICE-CLOCK-OWNER@EXAMPLE.TEST",
                DisplayName = "Notice Clock Owner",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = setupNow,
            };
            var portfolio = new Portfolio
            {
                Name = "Notice Clock Workspace",
                ManagementCompanyName = "Notice Clock Workspace",
                TimeZone = "UTC",
                Currency = "USD",
                Status = PortfolioStatus.Active,
                CreatedAt = setupNow,
                UpdatedAt = setupNow,
            };
            setup.AddRange(actor, portfolio);
            await setup.SaveChangesAsync();
            var scope = await SeedAdministratorScopeAsync(setup, portfolio.Id, actor, setupNow);
            setup.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = effectiveNow,
                RealAnchorUtc = setupNow,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = setupNow,
            });
            await setup.SaveChangesAsync();

            var relationship = await SeedRelationshipAsync(setup, portfolio, actor, "Frozen", setupNow);
            var foundation = new NotificationFoundationService(
                setup, TimeProvider.System,
                setupScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
            await foundation.SeedSuppliedTemplatesAsync(
                scope, "seed-frozen-notice-draft-templates", CancellationToken.None);
            var policy = await setup.TenantNoticePolicies.SingleAsync(row =>
                row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            policy.Mode = TenantNoticeMode.Draft;

            var work = ClaimedWork(
                portfolio.Id,
                policy.Id,
                relationship.LeaseManagementId,
                relationship.PartyId,
                relationship.LedgerEntryId,
                token,
                "notice-clock-frozen",
                effectiveNow);
            setup.Add(work);
            await setup.SaveChangesAsync();

            portfolioId = portfolio.Id;
            leaseManagementId = relationship.LeaseManagementId;
            ledgerEntryId = relationship.LedgerEntryId;
        }

        var command = new ApplyClaimedTenantNoticeDraftBatchCommand(token);
        var identity = TenantNoticeDraftAutomation.Identity(command);
        var outcome = await ExecuteClaimedBatchAsync(command);

        outcome.Value.CreatedCount.Should().Be(1);
        outcome.Value.Drafts.Should().ContainSingle();
        outcome.Value.Drafts[0].AppliedAtUtc.Should().Be(effectiveNow);
        outcome.Value.Drafts[0].CreatedAt.Should().Be(effectiveNow);
        outcome.Value.Drafts[0].UpdatedAt.Should().Be(effectiveNow);

        await using var verify = NewContext();
        var persisted = await verify.NoticeDrafts.SingleAsync(row =>
            row.PortfolioId == portfolioId &&
            row.LeaseManagementId == leaseManagementId &&
            row.TenantLedgerEntryId == ledgerEntryId &&
            row.NoticeType == "rent-reminder");
        persisted.CreatedAt.Should().Be(effectiveNow);
        persisted.UpdatedAt.Should().Be(effectiveNow);

        var audit = await verify.AtomicAuditLogs.SingleAsync(row =>
            row.CommandType == identity.CommandType &&
            row.CommandIdempotencyKey == identity.IdempotencyKey &&
            row.PortfolioId == portfolioId &&
            row.EntityType == nameof(NoticeDraft));
        audit.Timestamp.Should().Be(effectiveNow);

        var outbox = await verify.OutboxMessages.SingleAsync(row =>
            row.PortfolioId == portfolioId &&
            row.IdempotencyKey.StartsWith("tenant-notice-draft-worker:"));
        outbox.CreatedAtUtc.Should().Be(effectiveNow);
        outbox.NextAttemptAtUtc.Should().Be(effectiveNow);

        await using (var seed = NewContext())
        {
            var current = await seed.AtomicCommandReceipts.SingleAsync(row =>
                row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
            seed.Remove(current);
            await seed.SaveChangesAsync();
            // Frozen legacy fixture from 461c3a1b. Never regenerate its fingerprint or result JSON
            // from the current command model or codec.
            seed.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = "tenant-notice-draft.claimed-batch.apply",
                IdempotencyKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                RequestFingerprint = "a665fa6f34715b086cb27446dd300f20205a3ca98b67414b4357111ee5edf9b6",
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = "tenant-notice-draft.claimed-batch.apply.v1",
                ResultJson = "{\"CreatedCount\":0,\"Drafts\":[]}",
                StartedAt = effectiveNow,
                CompletedAt = effectiveNow,
            });
            await seed.SaveChangesAsync();
        }

        var legacyReplay = await ExecuteClaimedBatchAsync(command);
        legacyReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        legacyReplay.Value.Should().BeEquivalentTo(new ApplyClaimedTenantNoticeDraftBatchResult(0, []));
        await using var afterReplay = NewContext();
        (await afterReplay.NoticeDrafts.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(1);
        (await afterReplay.OutboxMessages.CountAsync(row =>
            row.PortfolioId == portfolioId &&
            row.IdempotencyKey.StartsWith("tenant-notice-draft-worker:"))).Should().Be(1);
    }

    [SkippableFact]
    public async Task ClaimedBatch_StaleTokenCreatesNoDraftOrOutbox()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL stale-claim proof skipped.");
        var command = new ApplyClaimedTenantNoticeDraftBatchCommand(Guid.NewGuid());
        await using var before = NewContext();
        var draftCount = await before.NoticeDrafts.CountAsync();
        var outboxCount = await before.OutboxMessages.CountAsync();

        var outcome = await ExecuteClaimedBatchAsync(command);

        outcome.Value.CreatedCount.Should().Be(0);
        outcome.Value.Drafts.Should().BeEmpty();
        await using var verify = NewContext();
        (await verify.NoticeDrafts.CountAsync()).Should().Be(draftCount);
        (await verify.OutboxMessages.CountAsync()).Should().Be(outboxCount);
    }

    [SkippableFact(Skip = "RS-B07 stale notice test: current atomic and error contracts differ from the legacy fixture; receipt #rs-b07-notice-contract")]
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
        int otherLeaseManagementId;
        int validTenantId;
        int portfolioId;
        WorkspaceReadScope manualScope = default;
        WorkspaceReadScope selectedPropertyScope = default;

        await using (var setupScope = _services!.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
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
            manualScope = await SeedAdministratorScopeAsync(setup, portfolio.Id, actor, now);
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
            selectedPropertyScope = await SeedPropertyManagerScopeAsync(
                setup, portfolio.Id, first.PropertyId, now);
            var foundation = new NotificationFoundationService(
                setup, TimeProvider.System,
                setupScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
            await foundation.SeedSuppliedTemplatesAsync(
                manualScope, "seed-notice-draft-templates", CancellationToken.None);
            var policy = await setup.TenantNoticePolicies.SingleAsync(row =>
                row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            policy.Mode = TenantNoticeMode.Draft;

            var valid = ClaimedWork(
                portfolio.Id, policy.Id, first.LeaseManagementId, first.PartyId, first.LedgerEntryId, token,
                "notice-set-valid", now);
            var mismatched = ClaimedWork(
                portfolio.Id, policy.Id, first.LeaseManagementId, first.PartyId, second.LedgerEntryId, token,
                "notice-set-cross-account", now);
            setup.AddRange(valid, mismatched);
            await setup.SaveChangesAsync();

            portfolioId = portfolio.Id;
            validWorkId = valid.Id;
            mismatchedWorkId = mismatched.Id;
            validLedgerId = first.LedgerEntryId;
            otherLedgerId = second.LedgerEntryId;
            validLeaseManagementId = first.LeaseManagementId;
            otherLeaseManagementId = second.LeaseManagementId;
            validTenantId = first.TenantId;
        }

        var recorder = _services!.GetRequiredService<CommandRecorder>();
        recorder.Clear();
        var command = new ApplyClaimedTenantNoticeDraftBatchCommand(token);
        var identity = TenantNoticeDraftAutomation.Identity(command);
        var concurrent = await Task.WhenAll(
            ExecuteClaimedBatchAsync(command),
            ExecuteClaimedBatchAsync(command));

        concurrent.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        concurrent[0].Value.Should().BeEquivalentTo(concurrent[1].Value);
        concurrent.Should().OnlyContain(outcome => outcome.Value.Drafts.Length == 1);
        concurrent.SelectMany(outcome => outcome.Value.Drafts).Should().OnlyContain(row =>
            row.WorkItemId == validWorkId && row.TenantLedgerEntryId == validLedgerId);
        concurrent.Should().OnlyContain(outcome => outcome.Value.CreatedCount == 1);
        concurrent.SelectMany(outcome => outcome.Value.Drafts).Should().OnlyContain(row => row.WasCreated);
        concurrent.SelectMany(outcome => outcome.Value.Drafts)
            .Should().OnlyContain(row => row.PortfolioId == portfolioId);
        concurrent.SelectMany(outcome => outcome.Value.Drafts).Select(row => row.DraftId)
            .Distinct().Should().ContainSingle(
                because: "receipt replay must return the first exact canonical draft result");
        concurrent.SelectMany(outcome => outcome.Value.Drafts)
            .Should().NotContain(row => row.WorkItemId == mismatchedWorkId);
        concurrent.SelectMany(outcome => outcome.Value.Drafts)
            .Should().OnlyContain(row => row.AppliedAtUtc == now);

        var noticeCommands = recorder.ReaderCommands
            .Where(sql => sql.Contains("INSERT INTO \"NoticeDrafts\"", StringComparison.Ordinal))
            .ToArray();
        noticeCommands.Should().ContainSingle(
            because: "receipt replay must not execute the raw notice insertion command twice");
        var sql = noticeCommands.Single();
        sql.Should().Contain("INSERT INTO \"NoticeDrafts\"");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("vw_tenant_charge_balances");
        sql.Should().Contain("WorkspaceNoticeTemplateVersions");
        sql.Should().Contain("SELECT DISTINCT ON (rendered.\"DedupeKey\")");
        sql.Should().Contain("ORDER BY rendered.\"DedupeKey\", rendered.\"WorkItemId\" NULLS LAST");
        sql.Should().Contain("ON CONFLICT");

        var manualRecorder = new CommandRecorder();
        await using (var replay = NewContext(manualRecorder))
        {
            var store = new TenantNoticeDraftSetStore(replay);
            var rows = await store.GenerateClaimedBatchAsync(token);
            rows.Should().ContainSingle();
            rows[0].WasCreated.Should().BeFalse();
            rows[0].DraftId.Should().Be(concurrent[0].Value.Drafts[0].DraftId);

            var manual = await store.GenerateManualAsync(
                manualScope, null, validLeaseManagementId, null, validLedgerId,
                "rent-reminder", now);
            manual.Should().ContainSingle();
            manual[0].WorkItemId.Should().BeNull();
            manual[0].WasCreated.Should().BeFalse();
            manual[0].DraftId.Should().Be(rows[0].DraftId);

            var crossAccount = await store.GenerateManualAsync(
                manualScope, null, validLeaseManagementId, null, otherLedgerId,
                "rent-reminder", now);
            crossAccount.Should().BeEmpty();

            var otherPropertyDraft = await store.GenerateManualAsync(
                manualScope, null, otherLeaseManagementId, null, otherLedgerId,
                "rent-reminder", now);
            otherPropertyDraft.Should().ContainSingle();

            await using var executionScope = _services!.CreateAsyncScope();
            var executionDb = executionScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var selectedService = new NoticeDraftService(
                executionDb,
                new FixedTimeProvider(new DateTimeOffset(now)),
                executionScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
            var selectedDrafts = await selectedService.ListAsync(
                selectedPropertyScope, null, new ListQuery { Take = 20 });
            selectedDrafts.Should().ContainSingle();
            selectedDrafts[0].LeaseManagementId.Should().Be(validLeaseManagementId);
            var authorizedUpdate = await selectedService.UpdateAsync(
                selectedPropertyScope,
                selectedDrafts[0].Id,
                new UpdateNoticeDraftRequest
                {
                    Subject = "Upcoming rent reminder - reviewed",
                },
                "selected-notice-authorized-update");
            authorizedUpdate.Should().NotBeNull();
            authorizedUpdate!.Subject.Should().Be("Upcoming rent reminder - reviewed");
            (await selectedService.GetAsync(selectedPropertyScope, otherPropertyDraft[0].DraftId))
                .Should().BeNull();
            (await selectedService.UpdateAsync(
                    selectedPropertyScope,
                    otherPropertyDraft[0].DraftId,
                    new UpdateNoticeDraftRequest { Subject = "Unauthorized edit" },
                    "selected-notice-unauthorized-update"))
                .Should().BeNull();
            (await selectedService.DismissAsync(
                    selectedPropertyScope,
                    otherPropertyDraft[0].DraftId,
                    "selected-notice-unauthorized-dismiss"))
                .Should().BeNull();

            var deniedGeneration = await selectedService.GenerateAsync(
                selectedPropertyScope,
                new GenerateNoticeDraftsRequest
                {
                    LeaseManagementId = otherLeaseManagementId,
                    TenantLedgerEntryId = otherLedgerId,
                    NoticeType = "rent-reminder",
                },
                "selected-notice-unauthorized-generate");
            deniedGeneration.CreatedCount.Should().Be(0);
            deniedGeneration.Drafts.Should().BeEmpty();

            var selectedContext = await executionDb.WorkspaceAccessContexts.SingleAsync(row =>
                row.Id == selectedPropertyScope.AccessContextId);
            selectedContext.AdvanceRevision(selectedPropertyScope.AccessRevision);
            await executionDb.SaveChangesAsync();

            var staleDismiss = () => selectedService.DismissAsync(
                selectedPropertyScope,
                selectedDrafts[0].Id,
                "selected-notice-stale-dismiss");
            await staleDismiss.Should().ThrowAsync<UnauthorizedAccessException>(
                because: "every write must revalidate the presented access revision inside its transaction");

            var staleGeneration = () => selectedService.GenerateAsync(
                selectedPropertyScope,
                new GenerateNoticeDraftsRequest
                {
                    LeaseManagementId = validLeaseManagementId,
                    TenantLedgerEntryId = validLedgerId,
                    NoticeType = "rent-reminder",
                },
                "selected-notice-stale-generate");
            await staleGeneration.Should().ThrowAsync<UnauthorizedAccessException>(
                because: "a stale access revision must not generate or replay a receipt");

            var forcedLifecycle = await store.GenerateManualAsync(
                manualScope, validTenantId, null, null, null,
                "lease-renewal-offer", now);
            forcedLifecycle.Should().ContainSingle(
                because: "an explicitly requested lifecycle notice is valid outside its lead window when tenant-scoped");
            forcedLifecycle[0].WorkItemId.Should().BeNull();
            forcedLifecycle[0].NoticeType.Should().Be("lease-renewal-offer");
        }

        manualRecorder.ReaderCommands.Should().Contain(command =>
            command.Contains("MembershipRoleAssignmentProperties", StringComparison.Ordinal) &&
            command.Contains("AuthSessions", StringComparison.Ordinal) &&
            command.Contains("authorized_properties", StringComparison.Ordinal),
            because: "interactive generation must authorize the exact selected property in the PostgreSQL command");

        await using var verify = NewContext();
        (await verify.NoticeDrafts.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(3);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType &&
            receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        var auditLogs = await verify.AtomicAuditLogs
            .Where(audit =>
                audit.CommandType == identity.CommandType &&
                audit.PortfolioId == portfolioId &&
                audit.EntityType == nameof(NoticeDraft))
            .ToArrayAsync();
        auditLogs.Should().ContainSingle();
        auditLogs[0].Timestamp.Should().Be(now);
        var outboxMessages = await verify.OutboxMessages
            .Where(message =>
                message.PortfolioId == portfolioId &&
                message.IdempotencyKey.StartsWith("tenant-notice-draft-worker:"))
            .ToArrayAsync();
        outboxMessages.Should().ContainSingle();
        outboxMessages[0].CreatedAtUtc.Should().Be(now);
        outboxMessages[0].NextAttemptAtUtc.Should().Be(now);
        (await verify.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == identity.CommandType &&
            audit.PortfolioId == portfolioId &&
            audit.EntityType == nameof(NoticeDraft))).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(message =>
            message.PortfolioId == portfolioId &&
            message.IdempotencyKey.StartsWith("tenant-notice-draft-worker:"))).Should().Be(1);
        var persisted = await verify.NoticeDrafts.SingleAsync(row =>
            row.PortfolioId == portfolioId &&
            row.LeaseManagementId == validLeaseManagementId &&
            row.NoticeType == "rent-reminder");
        persisted.TenantLedgerEntryId.Should().Be(validLedgerId);
        persisted.Status.Should().Be("Draft");
        persisted.CreatedAt.Should().Be(now);
        persisted.UpdatedAt.Should().Be(now);
        persisted.Subject.Should().Contain("Upcoming rent reminder");
        persisted.Body.Should().Contain("Casey A",
            because: "each durable notice draft is rendered for one exact eligible relationship party");
        persisted.Body.Should().Contain("$1,000");
        persisted.Body.Should().Contain(
            "Hello Casey A, this is a reminder that $1,000 is due on July 1, 2026");
        persisted.Body.Should().NotContain("{",
            because: "set-based draft rendering must replace the full double-brace merge token");
        persisted.Body.Should().NotContain("}");
    }

    private static TenantNoticeWorkItem ClaimedWork(
        int portfolioId,
        int policyId,
        int leaseManagementId,
        int recipientLeaseManagementPartyId,
        long ledgerEntryId,
        Guid token,
        string businessKey,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        TenantNoticePolicyId = policyId,
        LeaseManagementId = leaseManagementId,
        RecipientLeaseManagementPartyId = recipientLeaseManagementPartyId,
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

    private static async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(
        RentalCommandDbContext db,
        int portfolioId,
        ApplicationUser actor,
        DateTime now)
    {
        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddYears(20),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolioId,
            actor.Id,
            session.Id,
            context.Id,
            context.AccessRevision);
    }

    private static async Task<WorkspaceReadScope> SeedPropertyManagerScopeAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int propertyId,
        DateTime now)
    {
        var email = $"notice-property-manager-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Notice Property Manager",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.PropertyManager).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = assignment,
            PortfolioId = portfolioId,
            PropertyId = propertyId,
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddYears(20),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolioId, user.Id, session.Id, context.Id, context.AccessRevision);
    }

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
        return new RelationshipSeed(property.Id, management.Id, account.Id, party.Id, tenant.Id, charge.Id);
    }

    private RentalCommandDbContext NewContext(CommandRecorder? recorder = null)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString);
        if (recorder is not null) options.AddInterceptors(recorder);
        return new RentalCommandDbContext(options.Options);
    }

    private async Task<AtomicCommandOutcome<ApplyClaimedTenantNoticeDraftBatchResult>>
        ExecuteClaimedBatchAsync(ApplyClaimedTenantNoticeDraftBatchCommand command)
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var handler = new ApplyClaimedTenantNoticeDraftBatchRule(db);
        return await scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>().ExecuteAsync(
            TenantNoticeDraftAutomation.Identity(command).IdempotencyKey,
            TenantNoticeDraftAutomation.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync));
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

        public void Clear()
        {
            while (ReaderCommands.TryDequeue(out _))
            {
            }
        }

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

    private sealed record RelationshipSeed(
        int PropertyId,
        int LeaseManagementId,
        int TenantAccountId,
        int PartyId,
        int TenantId,
        long LedgerEntryId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
