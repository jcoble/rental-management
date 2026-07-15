using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof that supplied notice content is migration-owned and concurrent workspace
/// bootstraps converge on one immutable v1 copy per system key. SQLite cannot prove ON CONFLICT or
/// transaction participation.
/// </summary>
public sealed class SuppliedNoticeTemplateBaselineTests : IAsyncLifetime
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
                .WithDatabase("rentalcommand_supplied_notices")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
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
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicNotificationMutationCommand,
            AtomicNotificationMutationResult,
            AtomicNotificationMutationHandler>();
        services.AddAtomicCommandHandler<
            AtomicNoticeDeliveryCommand,
            AtomicNoticeDeliveryResult,
            AtomicNoticeDeliveryHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_connectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SeedSuppliedTemplates_ConcurrentCalls_CreateExactlyOneExactWorkspaceCopy()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice baseline proof skipped.");

        int portfolioId;
        int actorUserId;
        WorkspaceReadScope scope = default;
        await using (var setup = NewContext())
        {
            var now = DateTime.UtcNow;
            var actor = new ApplicationUser
            {
                UserName = "template-owner@example.test",
                NormalizedUserName = "TEMPLATE-OWNER@EXAMPLE.TEST",
                Email = "template-owner@example.test",
                NormalizedEmail = "TEMPLATE-OWNER@EXAMPLE.TEST",
                DisplayName = "Template Owner",
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Template Workspace",
                ManagementCompanyName = "Template Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.Users.Add(actor);
            setup.Portfolios.Add(portfolio);
            await setup.SaveChangesAsync();
            portfolioId = portfolio.Id;
            actorUserId = actor.Id;
            scope = await SeedAdministratorScopeAsync(setup, portfolio, actor, now);
        }

        const int callers = 8;
        await Task.WhenAll(Enumerable.Range(0, callers).Select(index => Task.Run(async () =>
        {
            await using var db = NewContext();
            var service = new NotificationFoundationService(db, TimeProvider.System, Atomic);
            await service.SeedSuppliedTemplatesAsync(
                scope, $"seed-concurrent-{index}", CancellationToken.None);
        })));

        await using var verify = NewContext();
        var workspaceCount = await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            .CountAsync(row => row.PortfolioId == portfolioId);
        var defaultPolicyCount = await verify.TenantNoticePolicies.AsNoTracking()
            .CountAsync(row => row.PortfolioId == portfolioId && row.Mode == TenantNoticeMode.Draft);
        var mismatchedPolicyBindings = await (
            from policy in verify.TenantNoticePolicies.AsNoTracking()
            join template in verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
                on new { TemplateId = policy.WorkspaceNoticeTemplateVersionId, policy.PortfolioId }
                equals new { TemplateId = template.Id, template.PortfolioId }
            where policy.PortfolioId == portfolioId &&
                (policy.AutomationKey != template.SystemKey ||
                 policy.IncludeOccupant || !policy.IncludePrimaryTenant || !policy.IncludeCoTenant)
            select policy.Id).CountAsync();
        var mismatchedCopies = await (
            from workspace in verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            join system in verify.SystemNoticeTemplateVersions.AsNoTracking()
                on workspace.BasedOnSystemTemplateVersionId equals system.Id
            where workspace.PortfolioId == portfolioId &&
                (workspace.Version != SuppliedNoticeTemplateBaseline.Version ||
                 workspace.SystemKey != system.SystemKey ||
                 workspace.Subject != system.Subject ||
                 workspace.Body != system.Body ||
                 workspace.JurisdictionCode != system.JurisdictionCode ||
                 workspace.IsCustomized ||
                 workspace.CreatedByUserId != actorUserId)
            select workspace.Id).CountAsync();

        workspaceCount.Should().Be(SuppliedNoticeTemplateBaseline.V1.Count);
        defaultPolicyCount.Should().Be(SuppliedNoticeTemplateBaseline.V1.Count);
        mismatchedPolicyBindings.Should().Be(0);
        mismatchedCopies.Should().Be(0);
    }

    [SkippableFact]
    public async Task CreateTemplateVersion_AppendsAndBindsPolicyInOneTransaction()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice template binding proof skipped.");

        var now = DateTime.UtcNow;
        int portfolioId;
        int originalTemplateId;
        WorkspaceReadScope scope = default;
        await using (var setup = NewContext())
        {
            var actor = new ApplicationUser
            {
                UserName = "template-editor@example.test",
                NormalizedUserName = "TEMPLATE-EDITOR@EXAMPLE.TEST",
                Email = "template-editor@example.test",
                NormalizedEmail = "TEMPLATE-EDITOR@EXAMPLE.TEST",
                DisplayName = "Template Editor",
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Template Binding Workspace",
                ManagementCompanyName = "Template Binding Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.AddRange(actor, portfolio);
            await setup.SaveChangesAsync();
            scope = await SeedAdministratorScopeAsync(setup, portfolio, actor, now);
            var service = new NotificationFoundationService(setup, TimeProvider.System, Atomic);
            await service.SeedSuppliedTemplatesAsync(
                scope, "seed-template-binding", CancellationToken.None);
            var original = await setup.TenantNoticePolicies.AsNoTracking().SingleAsync(row =>
                row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            portfolioId = portfolio.Id;
            originalTemplateId = original.WorkspaceNoticeTemplateVersionId;
        }

        TenantNoticePolicyResponse saved;
        await using (var command = NewContext())
        {
            var service = new NotificationFoundationService(command, TimeProvider.System, Atomic);
            saved = await service.CreateTemplateVersionAsync(
                scope,
                "rent-reminder",
                new CreateWorkspaceNoticeTemplateVersionRequest(
                    "Your rent is coming due",
                    "Hello {{tenant_name}}, this is your rent reminder.",
                    null,
                    false),
                "template-version-rent-reminder",
                CancellationToken.None);
        }

        saved.TemplateVersion.Should().Be(2);
        saved.TemplateBody.Should().Contain("{{tenant_name}}");
        saved.TemplateIsCustomized.Should().BeTrue();
        saved.WorkspaceNoticeTemplateVersionId.Should().NotBe(originalTemplateId);

        await using var verify = NewContext();
        var policyTemplateId = await verify.TenantNoticePolicies.AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId && row.AutomationKey == "rent-reminder")
            .Select(row => row.WorkspaceNoticeTemplateVersionId)
            .SingleAsync();
        policyTemplateId.Should().Be(saved.WorkspaceNoticeTemplateVersionId);
        (await verify.WorkspaceNoticeTemplateVersions.AsNoTracking().CountAsync(row =>
            row.PortfolioId == portfolioId && row.SystemKey == "rent-reminder")).Should().Be(2);
        (await verify.WorkspaceNoticeTemplateVersions.AsNoTracking().SingleAsync(row =>
            row.Id == originalTemplateId)).Version.Should().Be(1);
    }

    [SkippableFact]
    public async Task CandidateGeneration_InsertsExactRentAndLateChargeOnceAcrossReplay()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL tenant-notice candidate proof skipped.");

        var now = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        int portfolioId;
        long upcomingRentId;
        long overdueRentId;
        WorkspaceReadScope scope = default;
        await using (var setup = NewContext())
        {
            var actor = new ApplicationUser
            {
                UserName = "notice-candidates@example.test",
                NormalizedUserName = "NOTICE-CANDIDATES@EXAMPLE.TEST",
                Email = "notice-candidates@example.test",
                NormalizedEmail = "NOTICE-CANDIDATES@EXAMPLE.TEST",
                DisplayName = "Notice Candidate Owner",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Notice Candidate Workspace",
                ManagementCompanyName = "Notice Candidate Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                TimeZone = "UTC",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.AddRange(actor, portfolio);
            await setup.SaveChangesAsync();
            scope = await SeedAdministratorScopeAsync(setup, portfolio, actor, DateTime.UtcNow);
            setup.SimulationClocks.Add(new SimulationClock
            {
                Id = 1,
                Mode = ClockMode.Frozen,
                SimAnchorUtc = now,
                RealAnchorUtc = now,
                TimeZoneId = "UTC",
                UpdatedAtRealUtc = now,
            });

            var property = new Property
            {
                PortfolioId = portfolio.Id,
                Name = "Candidate House",
                AddressLine1 = "10 Candidate Way",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.Add(property);
            await setup.SaveChangesAsync();
            var unit = new Unit
            {
                PortfolioId = portfolio.Id,
                PropertyId = property.Id,
                UnitNumber = "1",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var tenant = new Tenant
            {
                PortfolioId = portfolio.Id,
                FirstName = "Casey",
                LastName = "Candidate",
                Email = "casey@example.test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var template = new DocumentTemplate
            {
                PortfolioId = portfolio.Id,
                Name = "Candidate Lease Template",
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var issuedFile = StoredFile(portfolio.Id, "candidate-issued.pdf", now);
            var executedFile = StoredFile(portfolio.Id, "candidate-executed.pdf", now);
            setup.AddRange(unit, tenant, template, issuedFile, executedFile);
            await setup.SaveChangesAsync();
            var source = new LegalDocumentSourceVersion
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                SourceKind = LegalDocumentSourceKind.AuthoredTemplateSnapshot,
                BusinessKey = $"template:{template.Id}:v1",
                DocumentTemplateId = template.Id,
                DocumentTemplateVersion = 1,
                RendererKey = "lease-agreement-overlay",
                RendererVersion = 1,
                SnapshotPayload = "{}",
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            };
            setup.Add(source);
            await setup.SaveChangesAsync();
            var issuedArtifact = LegalArtifact(
                portfolio.Id, actor.Id, issuedFile.Id, LegalDocumentArtifactKind.IssuedAgreement,
                "candidate-issued", now);
            var executedArtifact = LegalArtifact(
                portfolio.Id, actor.Id, executedFile.Id, LegalDocumentArtifactKind.ExecutedAgreement,
                "candidate-executed", now);
            setup.AddRange(issuedArtifact, executedArtifact);
            await setup.SaveChangesAsync();
            var management = new LeaseManagement
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                PropertyId = property.Id,
                UnitId = unit.Id,
                RelationshipNumber = "LM-NOTICE-CANDIDATES",
                PossessionGivenAtUtc = now.AddMonths(-1),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actor.Id,
                RowVersion = Guid.NewGuid(),
            };
            setup.Add(management);
            await setup.SaveChangesAsync();
            var account = new TenantAccount
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                LeaseManagementId = management.Id,
                AccountNumber = "TA-NOTICE-CANDIDATES",
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
                AgreementNumber = "AGR-NOTICE-CANDIDATES",
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
                IssuedArtifactId = issuedArtifact.Id,
                IssuedAtUtc = now.AddMonths(-2),
                ExecutedArtifactId = executedArtifact.Id,
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
                ChangeReason = "Candidate integration test",
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            };
            setup.AddRange(account, agreement, party);
            await setup.SaveChangesAsync();
            var upcomingRent = LedgerCharge(
                portfolio.Id, account.Id, agreement.Id, actor.Id, TenantLedgerEntryType.RentCharge,
                1_000m, new DateOnly(2026, 7, 15), "candidate-upcoming-rent", now);
            var overdueRent = LedgerCharge(
                portfolio.Id, account.Id, agreement.Id, actor.Id, TenantLedgerEntryType.RentCharge,
                1_000m, new DateOnly(2026, 7, 1), "candidate-overdue-rent", now);
            var overdueLateFee = LedgerCharge(
                portfolio.Id, account.Id, agreement.Id, actor.Id, TenantLedgerEntryType.LateFeeCharge,
                50m, new DateOnly(2026, 7, 1), "candidate-overdue-late-fee", now);
            setup.AddRange(upcomingRent, overdueRent, overdueLateFee);
            await setup.SaveChangesAsync();

            var foundation = new NotificationFoundationService(setup, TimeProvider.System, Atomic);
            await foundation.SeedSuppliedTemplatesAsync(
                scope, "seed-candidate-generation", CancellationToken.None);
            portfolioId = portfolio.Id;
            upcomingRentId = upcomingRent.Id;
            overdueRentId = overdueRent.Id;
        }

        await using (var command = NewContext())
        {
            var service = new TenantNoticeCandidateGenerationService(command, InfrastructureWrites);
            (await service.GenerateDueAsync()).Should().Be(2);
            (await service.GenerateDueAsync()).Should().Be(0);
        }

        await using var verify = NewContext();
        var candidates = await (
                from work in verify.TenantNoticeWorkItems.AsNoTracking()
                join policy in verify.TenantNoticePolicies.AsNoTracking()
                    on new { work.TenantNoticePolicyId, work.PortfolioId }
                    equals new { TenantNoticePolicyId = policy.Id, policy.PortfolioId }
                where work.PortfolioId == portfolioId
                orderby policy.AutomationKey
                select new { policy.AutomationKey, work.TenantLedgerEntryId, work.BusinessKey })
            .ToListAsync();
        candidates.Should().HaveCount(2);
        candidates.Should().ContainSingle(row =>
            row.AutomationKey == "rent-reminder" && row.TenantLedgerEntryId == upcomingRentId);
        candidates.Should().ContainSingle(row =>
            row.AutomationKey == "late-rent-late-fee" && row.TenantLedgerEntryId == overdueRentId);
        candidates.Select(row => row.BusinessKey).Should().OnlyHaveUniqueItems();
    }

    [SkippableFact]
    public async Task ApproveAndQueue_UsesDispatchableOutboxTypes_AndProjectsDurableStatus()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL tenant-notice delivery proof skipped.");

        var now = DateTime.UtcNow;
        int portfolioId;
        int draftId;
        long workItemId;
        int baselineOutboxCount;
        WorkspaceReadScope scope = default;
        var claimToken = Guid.NewGuid();
        await using (var setup = NewContext())
        {
            var actor = new ApplicationUser
            {
                UserName = "notice-delivery@example.test",
                NormalizedUserName = "NOTICE-DELIVERY@EXAMPLE.TEST",
                Email = "notice-delivery@example.test",
                NormalizedEmail = "NOTICE-DELIVERY@EXAMPLE.TEST",
                DisplayName = "Notice Delivery Owner",
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Notice Delivery Workspace",
                ManagementCompanyName = "Notice Delivery Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                TimeZone = "America/New_York",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.AddRange(actor, portfolio);
            await setup.SaveChangesAsync();
            scope = await SeedAdministratorScopeAsync(setup, portfolio, actor, now);

            var property = new Property
            {
                PortfolioId = portfolio.Id,
                Name = "Notice House",
                AddressLine1 = "1 Delivery Way",
                City = "Akron",
                State = "OH",
                PostalCode = "44301",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.Add(property);
            await setup.SaveChangesAsync();
            var unit = new Unit
            {
                PortfolioId = portfolio.Id,
                PropertyId = property.Id,
                UnitNumber = "1",
                Bedrooms = 1,
                Bathrooms = 1,
                MarketRent = 1_000m,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var tenant = new Tenant
            {
                PortfolioId = portfolio.Id,
                FirstName = "Taylor",
                LastName = "Recipient",
                Email = "taylor@example.test",
                Phone = "+13305550123",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.AddRange(unit, tenant);
            await setup.SaveChangesAsync();
            var management = new LeaseManagement
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                PropertyId = property.Id,
                UnitId = unit.Id,
                RelationshipNumber = "LM-NOTICE-DELIVERY",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actor.Id,
                RowVersion = Guid.NewGuid(),
            };
            setup.Add(management);
            await setup.SaveChangesAsync();
            var account = new TenantAccount
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                LeaseManagementId = management.Id,
                AccountNumber = "TA-NOTICE-DELIVERY",
                Currency = "USD",
                OpenedAtUtc = now,
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            };
            var party = new LeaseManagementParty
            {
                PortfolioId = portfolio.Id,
                LeaseManagementId = management.Id,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
                ChangeReason = "Delivery integration test",
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            };
            setup.AddRange(account, party);
            await setup.SaveChangesAsync();

            var foundation = new NotificationFoundationService(setup, TimeProvider.System, Atomic);
            await foundation.SeedSuppliedTemplatesAsync(
                scope, "seed-notice-delivery", CancellationToken.None);
            var policy = await setup.TenantNoticePolicies
                .SingleAsync(row => row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            policy.Mode = TenantNoticeMode.Auto;
            policy.SendSms = true;
            var draft = new NoticeDraft
            {
                PortfolioId = portfolio.Id,
                LeaseManagementId = management.Id,
                TenantAccountId = account.Id,
                RecipientLeaseManagementPartyId = party.Id,
                PropertyId = property.Id,
                NoticeType = "rent-reminder",
                Subject = "Rent reminder",
                Body = "Your rent is due soon.",
                Reason = "Scheduled reminder",
                TriggerDate = now,
                TenantNoticePolicyId = policy.Id,
                WorkspaceNoticeTemplateVersionId = policy.WorkspaceNoticeTemplateVersionId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var workItem = new TenantNoticeWorkItem
            {
                PortfolioId = portfolio.Id,
                TenantNoticePolicyId = policy.Id,
                LeaseManagementId = management.Id,
                DueAtUtc = now,
                Status = TenantNoticeWorkStatus.Claimed,
                BusinessKey = $"tenant-notice:test:{Guid.NewGuid():N}",
                AttemptCount = 1,
                ClaimOwner = "integration-test",
                ClaimToken = claimToken,
                ClaimExpiresAtUtc = now.AddMinutes(5),
                CreatedAtUtc = now,
            };
            setup.AddRange(draft, workItem);
            await setup.SaveChangesAsync();
            portfolioId = portfolio.Id;
            draftId = draft.Id;
            workItemId = workItem.Id;
            baselineOutboxCount = await setup.OutboxMessages.CountAsync(row =>
                row.PortfolioId == portfolio.Id);
        }

        await using (var stale = NewContext())
        {
            var foundation = new NotificationFoundationService(stale, TimeProvider.System, Atomic);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForAutomation(portfolioId),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                new RentalCommand.Api.DTOs.TenantNoticeWorkFence(workItemId, Guid.NewGuid()),
                $"integration:tenant-notice-work:{workItemId}:stale",
                CancellationToken.None);
            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        await using (var rolledBack = NewContext())
        {
            (await rolledBack.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(0);
            (await rolledBack.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(baselineOutboxCount);
            (await rolledBack.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(0);
            (await rolledBack.PortalMessages.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(0);
            (await rolledBack.NoticeDrafts.SingleAsync(row => row.Id == draftId)).Status.Should().Be("Draft");
            (await rolledBack.TenantNoticeWorkItems.SingleAsync(row => row.Id == workItemId)).Status
                .Should().Be(TenantNoticeWorkStatus.Claimed);
        }

        await using (var command = NewContext())
        {
            var foundation = new NotificationFoundationService(command, TimeProvider.System, Atomic);
            await foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForAutomation(portfolioId),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                new RentalCommand.Api.DTOs.TenantNoticeWorkFence(workItemId, claimToken),
                $"integration:tenant-notice-work:{workItemId}:approve",
                CancellationToken.None);
        }

        await using (var mutate = NewContext())
        {
            var outbox = await mutate.OutboxMessages
                .Where(row => row.PortfolioId == portfolioId)
                .OrderBy(row => row.MessageType)
                .ToListAsync();
            outbox.Select(row => row.MessageType).Should().BeEquivalentTo(["data-update", "email", "sms"]);
            outbox.Should().OnlyContain(row => row.MessageType != "TenantNoticeDelivery");
            (await mutate.PortalMessages.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(1);
            (await mutate.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(3);
            (await mutate.TenantNoticeWorkItems.SingleAsync(row => row.Id == workItemId)).Status
                .Should().Be(TenantNoticeWorkStatus.Completed);

            var portal = outbox.Single(row => row.MessageType == "data-update");
            portal.AcceptedAtUtc = now.AddMinutes(1);
            portal.LastAttemptAtUtc = now.AddMinutes(1);
            portal.AttemptCount = 1;
            portal.Provider = "postgres-notify";
            var email = outbox.Single(row => row.MessageType == "email");
            email.AttemptCount = 2;
            email.LastAttemptAtUtc = now.AddMinutes(2);
            email.NextAttemptAtUtc = now.AddMinutes(17);
            email.LastError = "Temporary provider outage";
            var sms = outbox.Single(row => row.MessageType == "sms");
            sms.AttemptCount = 5;
            sms.LastAttemptAtUtc = now.AddMinutes(3);
            sms.DeadLetteredAtUtc = now.AddMinutes(3);
            sms.FailureKind = OutboxFailureKind.Permanent;
            sms.LastError = "Invalid destination";
            await mutate.SaveChangesAsync();
        }

        await using (var verify = NewContext())
        {
            var foundation = new NotificationFoundationService(verify, TimeProvider.System, Atomic);
            var statuses = await foundation.ListDeliveryStatusesAsync(portfolioId, 50, CancellationToken.None);
            statuses.Should().HaveCount(3);
            statuses.Should().ContainSingle(row =>
                row.Channel == NoticeDeliveryChannel.TenantPortal &&
                row.Status == NoticeDeliveryState.Accepted);
            statuses.Should().ContainSingle(row =>
                row.Channel == NoticeDeliveryChannel.Email &&
                row.Status == NoticeDeliveryState.Retrying &&
                row.NextAttemptAtUtc != null);
            statuses.Should().ContainSingle(row =>
                row.Channel == NoticeDeliveryChannel.Sms &&
                row.Status == NoticeDeliveryState.PermanentlyFailed &&
                row.FailedAtUtc != null);
        }
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private IAtomicInfrastructureWriteGate InfrastructureWrites =>
        _services!.GetRequiredService<IAtomicInfrastructureWriteGate>();

    private static async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(
        RentalCommandDbContext db,
        Portfolio portfolio,
        ApplicationUser actor,
        DateTime now)
    {
        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
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
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolio.Id, actor.Id, session.Id, context.Id, context.AccessRevision);
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

    private static TenantLedgerEntry LedgerCharge(
        int portfolioId,
        int tenantAccountId,
        int agreementId,
        int actorUserId,
        TenantLedgerEntryType entryType,
        decimal amount,
        DateOnly dueOn,
        string businessKey,
        DateTime now) => new()
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            TenantAccountId = tenantAccountId,
            EntryType = entryType,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = dueOn,
            DueOn = dueOn,
            PostedAtUtc = now,
            Description = businessKey,
            BusinessKey = businessKey,
            LeaseAgreementId = agreementId,
            CreatedByUserId = actorUserId,
        };
}
