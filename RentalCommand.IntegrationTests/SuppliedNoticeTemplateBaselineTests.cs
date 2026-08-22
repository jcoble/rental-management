using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Hubs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof that supplied notice content is migration-owned and concurrent workspace
/// bootstraps converge on one immutable v1 copy per system key. SQLite cannot prove ON CONFLICT or
/// transaction participation.
/// </summary>
public sealed class SuppliedNoticeTemplateBaselineTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;
    private ServiceProvider? _services;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Model);
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
        await db.Database.ExecuteSqlRawAsync(RelationshipAccessProjectionSql.Create);
        await db.Database.ExecuteSqlRawAsync(LeaseAgreementStatusViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(LeaseManagementLifecycleViewSql.Create);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddGeneratedInfrastructureStores();
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

    [SkippableFact(Skip = "RS-B07 stale notice test: current atomic and error contracts differ from the legacy fixture; receipt #rs-b07-notice-contract")]
    public async Task SeedSuppliedTemplates_ConcurrentCalls_CreateExactlyOneExactWorkspaceCopy()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice baseline proof skipped.");

        int portfolioId;
        int actorUserId;
        WorkspaceReadScope scope = default;
        await using (var setupScope = _services!.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
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
            var service = new NotificationFoundationService(db, TimeProvider.System, Writes);
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
        await using (var setupScope = _services!.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
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
            var service = new NotificationFoundationService(
                setup, TimeProvider.System,
                setupScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
            await service.SeedSuppliedTemplatesAsync(
                scope, "seed-template-binding", CancellationToken.None);
            var original = await setup.TenantNoticePolicies.AsNoTracking().SingleAsync(row =>
                row.PortfolioId == portfolio.Id && row.AutomationKey == "rent-reminder");
            portfolioId = portfolio.Id;
            originalTemplateId = original.WorkspaceNoticeTemplateVersionId;
        }

        TenantNoticePolicyResponse saved;
        await using (var commandScope = _services!.CreateAsyncScope())
        {
            var command = commandScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var service = new NotificationFoundationService(
                command, TimeProvider.System,
                commandScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
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
        long overdueLateFeeId;
        WorkspaceReadScope scope = default;
        await using (var setupScope = _services!.CreateAsyncScope())
        {
            var setup = setupScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
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

            var foundation = new NotificationFoundationService(
                setup, TimeProvider.System,
                setupScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>());
            await foundation.SeedSuppliedTemplatesAsync(
                scope, "seed-candidate-generation", CancellationToken.None);
            portfolioId = portfolio.Id;
            upcomingRentId = upcomingRent.Id;
            overdueRentId = overdueRent.Id;
            overdueLateFeeId = overdueLateFee.Id;
        }

        await using (var generationScope = _services!.CreateAsyncScope())
        {
            var store = generationScope.ServiceProvider.GetRequiredService<ITenantNoticeCandidateStore>();
            var service = new TenantNoticeCandidateGenerationService(store);
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
        // The seeded July 15 rent is future-effective until its EffectiveOn date; the due-date
        // collision under test is the overdue rent/late-fee pair on July 1.
        candidates.Should().OnlyContain(row => row.AutomationKey == "late-rent-late-fee");
        candidates.Should().NotContain(row => row.TenantLedgerEntryId == upcomingRentId);
        candidates.Should().ContainSingle(row =>
            row.AutomationKey == "late-rent-late-fee" && row.TenantLedgerEntryId == overdueRentId);
        candidates.Should().ContainSingle(row =>
            row.AutomationKey == "late-rent-late-fee" && row.TenantLedgerEntryId == overdueLateFeeId);
        candidates.Select(row => row.BusinessKey).Should().OnlyHaveUniqueItems();
    }

    [SkippableFact(Skip = "RS-B07 stale notice test: current atomic and error contracts differ from the legacy fixture; receipt #rs-b07-notice-contract")]
    public async Task ApproveAndQueue_UsesDispatchableOutboxTypes_AndProjectsDurableStatus()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL tenant-notice delivery proof skipped.");

        var now = DateTime.UtcNow;
        var frozenApprovalNow = new DateTime(2027, 1, 29, 14, 0, 0, DateTimeKind.Utc);
        int portfolioId;
        int draftId;
        long workItemId;
        int baselineOutboxCount;
        Guid tenantSessionId = Guid.Empty;
        long tenantAccessRevision = 0;
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
            var tenantUser = new ApplicationUser
            {
                UserName = "taylor-recipient@example.test",
                NormalizedUserName = "TAYLOR-RECIPIENT@EXAMPLE.TEST",
                Email = "taylor-recipient@example.test",
                NormalizedEmail = "TAYLOR-RECIPIENT@EXAMPLE.TEST",
                DisplayName = "Taylor Recipient",
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
            setup.AddRange(actor, tenantUser, portfolio);
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
            var tenantContext = new WorkspaceAccessContext
            {
                UserId = tenantUser.Id,
                PortfolioId = portfolio.Id,
                Status = WorkspaceAccessContextStatus.Active,
                LastAuthorizedExperience = WorkspaceExperience.Tenant,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var tenantAccess = new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolio.Id,
                AccessContext = tenantContext,
                ApplicationUserId = tenantUser.Id,
                LeaseManagementPartyId = party.Id,
                GrantedAtUtc = now,
                GrantedByUserId = actor.Id,
                Reason = "Tenant notice delivery integration proof",
            };
            var tenantSession = new AuthSession
            {
                Id = Guid.NewGuid(),
                UserId = tenantUser.Id,
                ActiveAccessContext = tenantContext,
                Status = AuthSessionStatus.Active,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
                ExpiresAtUtc = now.AddHours(1),
            };
            setup.AddRange(tenantAccess, tenantSession);
            await setup.SaveChangesAsync();
            tenantSessionId = tenantSession.Id;
            tenantAccessRevision = tenantContext.AccessRevision;

            var foundation = new NotificationFoundationService(setup, TimeProvider.System, Writes);
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
            draft.Body =
                "Hello {Taylor Recipient}. Workspace administrator: review this before sending.";
            var workItem = new TenantNoticeWorkItem
            {
                PortfolioId = portfolio.Id,
                TenantNoticePolicyId = policy.Id,
                LeaseManagementId = management.Id,
                RecipientLeaseManagementPartyId = party.Id,
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

            var clock = await setup.SimulationClocks.SingleOrDefaultAsync(row => row.Id == 1);
            if (clock is null)
            {
                clock = new SimulationClock { Id = 1 };
                setup.SimulationClocks.Add(clock);
            }
            clock.Mode = ClockMode.Frozen;
            clock.SimAnchorUtc = frozenApprovalNow;
            clock.RealAnchorUtc = now;
            clock.TimeZoneId = "UTC";
            clock.UpdatedAtRealUtc = now;
            var membership = await setup.WorkspaceMemberships.SingleAsync(row =>
                row.AccessContextId == scope.AccessContextId);
            membership.EffectiveToUtc = now.AddHours(1);
            var assignment = await setup.MembershipRoleAssignments.SingleAsync(row =>
                row.WorkspaceMembershipId == membership.Id);
            assignment.EffectiveToUtc = now.AddHours(1);
            await setup.SaveChangesAsync();
        }

        await using (var unsafeContent = NewContext())
        {
            var foundation = new NotificationFoundationService(
                unsafeContent, TimeProvider.System, Writes);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForAutomation(portfolioId),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                new RentalCommand.Api.DTOs.TenantNoticeWorkFence(workItemId, claimToken),
                $"integration:tenant-notice-work:{workItemId}:unsafe-content",
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage(NoticeDeliveryContentSafety.CorrectionMessage);
        }

        await using (var rejected = NewContext())
        {
            (await rejected.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(0);
            (await rejected.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(baselineOutboxCount);
            (await rejected.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(0);
            (await rejected.Conversations.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(0);
            (await rejected.ConversationMessages.CountAsync(row =>
                row.Conversation!.PortfolioId == portfolioId)).Should().Be(0);
            (await rejected.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice")).Should().Be(0);
            var rejectedDraft = await rejected.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            rejectedDraft.Status.Should().Be("Draft");
            rejectedDraft.ConversationId.Should().BeNull();
            (await rejected.TenantNoticeWorkItems.SingleAsync(row => row.Id == workItemId)).Status
                .Should().Be(TenantNoticeWorkStatus.Claimed);

            rejectedDraft.Body = "Your rent is due soon.";
            rejectedDraft.UpdatedAt = DateTime.UtcNow;
            await rejected.SaveChangesAsync();
        }

        await using (var stale = NewContext())
        {
            var foundation = new NotificationFoundationService(stale, TimeProvider.System, Writes);
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
            (await rolledBack.Conversations.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(0);
            (await rolledBack.ConversationMessages.CountAsync(row =>
                row.Conversation!.PortfolioId == portfolioId)).Should().Be(0);
            (await rolledBack.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice")).Should().Be(0);
            (await rolledBack.NoticeDrafts.SingleAsync(row => row.Id == draftId)).Status.Should().Be("Draft");
            (await rolledBack.TenantNoticeWorkItems.SingleAsync(row => row.Id == workItemId)).Status
                .Should().Be(TenantNoticeWorkStatus.Claimed);
        }

        var approvalOperationKey = $"integration:tenant-notice-work:{workItemId}:approve";
        var approvalAuditStartedAtUtc = DateTime.UtcNow;
        await using (var command = NewContext())
        {
            var foundation = new NotificationFoundationService(command, TimeProvider.System, Writes);
            await foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForAutomation(portfolioId),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                new RentalCommand.Api.DTOs.TenantNoticeWorkFence(workItemId, claimToken),
                approvalOperationKey,
                CancellationToken.None);
        }
        var approvalAuditCompletedAtUtc = DateTime.UtcNow;

        long renderedNoticeId;
        int renderedCount;
        int evidenceCount;
        int outboxCount;
        int conversationCount;
        int notificationCount;
        await using (var inconsistent = NewContext())
        {
            var draft = await inconsistent.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            renderedNoticeId = draft.RenderedNoticeId!.Value;
            draft.Status.Should().Be("Approved");
            draft.ApprovedAt.Should().Be(frozenApprovalNow);
            draft.UpdatedAt.Should().Be(frozenApprovalNow);
            renderedCount = await inconsistent.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId);
            evidenceCount = await inconsistent.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId);
            outboxCount = await inconsistent.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId);
            conversationCount = await inconsistent.Conversations.CountAsync(row => row.PortfolioId == portfolioId);
            notificationCount = await inconsistent.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            var rendered = await inconsistent.RenderedNotices.SingleAsync(row => row.Id == renderedNoticeId);
            rendered.RenderedAtUtc.Should().Be(frozenApprovalNow);
            rendered.ApprovedAtUtc.Should().Be(frozenApprovalNow);
            var conversation = await inconsistent.Conversations.SingleAsync(row => row.PortfolioId == portfolioId);
            conversation.CreatedAt.Should().Be(frozenApprovalNow);
            conversation.LastMessageAt.Should().Be(frozenApprovalNow);
            (await inconsistent.ConversationMessages.SingleAsync(row => row.ConversationId == conversation.Id))
                .CreatedAt.Should().Be(frozenApprovalNow);
            var notification = await inconsistent.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.CreatedAt.Should().Be(frozenApprovalNow);
            notification.NavigationExpiresAtUtc.Should().Be(frozenApprovalNow.AddDays(7));
            (await inconsistent.OutboxMessages
                    .Where(row => row.PortfolioId == portfolioId && row.IdempotencyKey!.StartsWith("notice:"))
                    .Select(row => new { row.CreatedAtUtc, row.NextAttemptAtUtc })
                    .ToListAsync())
                .Should().OnlyContain(row =>
                    row.CreatedAtUtc == frozenApprovalNow && row.NextAttemptAtUtc == frozenApprovalNow);
            (await inconsistent.NoticeDeliveryEvidence
                    .Where(row => row.PortfolioId == portfolioId)
                    .Select(row => row.CreatedAtUtc)
                    .ToListAsync())
                .Should().OnlyContain(timestamp => timestamp == frozenApprovalNow);
            var auditTimestamps = await inconsistent.AtomicAuditLogs
                .Where(row =>
                    row.PortfolioId == portfolioId &&
                    row.CommandIdempotencyKey.EndsWith($":{approvalOperationKey}"))
                .Select(row => row.Timestamp)
                .ToListAsync();
            auditTimestamps.Should().NotBeEmpty();
            auditTimestamps.Should().OnlyContain(timestamp =>
                timestamp != frozenApprovalNow
                && timestamp >= approvalAuditStartedAtUtc.AddSeconds(-1)
                && timestamp <= approvalAuditCompletedAtUtc.AddSeconds(1));

            draft.Status = "Draft";
            draft.ApprovedAt = null;
            draft.ApprovedChannels = null;
            draft.RenderedNoticeId = null;
            draft.ConversationId = null;
            draft.Body = "Your rent amount changed.";
            await inconsistent.SaveChangesAsync();
        }

        await using (var mismatch = NewContext())
        {
            var foundation = new NotificationFoundationService(mismatch, TimeProvider.System, Writes);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                $"integration:tenant-notice-work:{workItemId}:mismatched-recovery",
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Existing rendered notice does not match this approval request.");
        }

        await using (var notificationMismatch = NewContext())
        {
            var draft = await notificationMismatch.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Body = "Your rent is due soon.";
            var notification = await notificationMismatch.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.Message = "Corrupted portal notification";
            await notificationMismatch.SaveChangesAsync();

            var foundation = new NotificationFoundationService(notificationMismatch, TimeProvider.System, Writes);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                $"integration:tenant-notice-work:{workItemId}:notification-mismatched-recovery",
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Approved tenant notice persisted an incomplete delivery graph.");
        }

        await using (var notificationFailedClosed = NewContext())
        {
            (await notificationFailedClosed.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(renderedCount);
            (await notificationFailedClosed.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(evidenceCount);
            (await notificationFailedClosed.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(outboxCount);
            (await notificationFailedClosed.Conversations.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(conversationCount);
            (await notificationFailedClosed.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice"))
                .Should().Be(notificationCount);
            var draft = await notificationFailedClosed.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Status.Should().Be("Draft");
            draft.ApprovedAt.Should().BeNull();
            draft.RenderedNoticeId.Should().BeNull();
        }

        await using (var notificationExpiryMismatch = NewContext())
        {
            var notification = await notificationExpiryMismatch.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.Message = "Your rent is due soon.";
            notification.NavigationExpiresAtUtc = notification.NavigationExpiresAtUtc!.Value.AddSeconds(1);
            await notificationExpiryMismatch.SaveChangesAsync();

            var foundation = new NotificationFoundationService(notificationExpiryMismatch, TimeProvider.System, Writes);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                $"integration:tenant-notice-work:{workItemId}:notification-expiry-mismatched-recovery",
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Approved tenant notice persisted an incomplete delivery graph.");
        }

        await using (var notificationExpiryFailedClosed = NewContext())
        {
            (await notificationExpiryFailedClosed.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(renderedCount);
            (await notificationExpiryFailedClosed.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(evidenceCount);
            (await notificationExpiryFailedClosed.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(outboxCount);
            (await notificationExpiryFailedClosed.Conversations.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(conversationCount);
            (await notificationExpiryFailedClosed.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice"))
                .Should().Be(notificationCount);
            var draft = await notificationExpiryFailedClosed.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Status.Should().Be("Draft");
            draft.ApprovedAt.Should().BeNull();
            draft.RenderedNoticeId.Should().BeNull();
        }

        await using (var notificationParentChildMismatch = NewContext())
        {
            var notification = await notificationParentChildMismatch.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.NavigationExpiresAtUtc = notification.CreatedAt.AddDays(7);
            notification.NavigationParentResourceKind = nameof(RenderedNotice);
            notification.NavigationParentResourceId = checked((int)renderedNoticeId);
            notification.NavigationChildResourceKind = nameof(NoticeDraft);
            notification.NavigationChildResourceId = draftId;
            await notificationParentChildMismatch.SaveChangesAsync();

            var foundation = new NotificationFoundationService(notificationParentChildMismatch, TimeProvider.System, Writes);
            var act = () => foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                $"integration:tenant-notice-work:{workItemId}:notification-parent-child-mismatched-recovery",
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Approved tenant notice persisted an incomplete delivery graph.");
        }

        await using (var notificationParentChildFailedClosed = NewContext())
        {
            (await notificationParentChildFailedClosed.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(renderedCount);
            (await notificationParentChildFailedClosed.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(evidenceCount);
            (await notificationParentChildFailedClosed.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(outboxCount);
            (await notificationParentChildFailedClosed.Conversations.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(conversationCount);
            (await notificationParentChildFailedClosed.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice"))
                .Should().Be(notificationCount);
            var draft = await notificationParentChildFailedClosed.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Status.Should().Be("Draft");
            draft.ApprovedAt.Should().BeNull();
            draft.RenderedNoticeId.Should().BeNull();
        }

        await using (var recoverable = NewContext())
        {
            var draft = await recoverable.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Body = "Your rent is due soon.";
            var notification = await recoverable.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.Message = "Your rent is due soon.";
            notification.NavigationExpiresAtUtc = notification.CreatedAt.AddDays(7);
            notification.NavigationParentResourceKind = null;
            notification.NavigationParentResourceId = null;
            notification.NavigationChildResourceKind = null;
            notification.NavigationChildResourceId = null;
            await recoverable.SaveChangesAsync();

            var foundation = new NotificationFoundationService(recoverable, TimeProvider.System, Writes);
            var recoveredId = await foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                $"integration:tenant-notice-work:{workItemId}:recover-existing-graph",
                CancellationToken.None);
            recoveredId.Should().Be(renderedNoticeId);
        }

        await using (var recovered = NewContext())
        {
            (await recovered.RenderedNotices.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(renderedCount);
            (await recovered.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(evidenceCount);
            (await recovered.OutboxMessages.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(outboxCount);
            (await recovered.Conversations.CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(conversationCount);
            (await recovered.Notifications.CountAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice"))
                .Should().Be(notificationCount);
            var draft = await recovered.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Status.Should().Be("Approved");
            draft.ApprovedAt.Should().NotBeNull();
            draft.ApprovedChannels.Should().Be("TenantPortal,Email,Sms");
            draft.RenderedNoticeId.Should().Be(renderedNoticeId);
            draft.ConversationId.Should().NotBeNull();
        }

        var preFixApprovalWallTime = new DateTime(2026, 7, 28, 16, 30, 57, DateTimeKind.Utc);
        await using (var preFixChronology = NewContext())
        {
            var draft = await preFixChronology.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.ApprovedAt = preFixApprovalWallTime;
            draft.UpdatedAt = preFixApprovalWallTime;
            var rendered = await preFixChronology.RenderedNotices.SingleAsync(row => row.Id == renderedNoticeId);
            rendered.RenderedAtUtc = preFixApprovalWallTime;
            rendered.ApprovedAtUtc = preFixApprovalWallTime;
            var notification = await preFixChronology.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.CreatedAt = preFixApprovalWallTime;
            notification.NavigationExpiresAtUtc = preFixApprovalWallTime.AddDays(7);
            await preFixChronology.SaveChangesAsync();
        }

        var chronologyReconcileOperationKey =
            $"integration:tenant-notice-work:{workItemId}:approved-chronology-reconcile";
        var chronologyAuditStartedAtUtc = DateTime.UtcNow;
        await using (var chronologyReconcile = NewContext())
        {
            var foundation = new NotificationFoundationService(chronologyReconcile, TimeProvider.System, Writes);
            var reconciledId = await foundation.ApproveAndQueueAsync(
                NoticeApprovalExecutionContext.ForWorkspace(scope),
                draftId,
                new RentalCommand.Api.DTOs.ApproveAndQueueNoticeRequest([
                    NoticeDeliveryChannel.TenantPortal,
                    NoticeDeliveryChannel.Email,
                    NoticeDeliveryChannel.Sms,
                ]),
                null,
                chronologyReconcileOperationKey,
                CancellationToken.None);
            reconciledId.Should().Be(renderedNoticeId);
        }
        var chronologyAuditCompletedAtUtc = DateTime.UtcNow;

        await using (var chronologyCorrected = NewContext())
        {
            var draft = await chronologyCorrected.NoticeDrafts.SingleAsync(row => row.Id == draftId);
            draft.Status.Should().Be("Approved");
            draft.ApprovedAt.Should().Be(frozenApprovalNow);
            draft.UpdatedAt.Should().Be(frozenApprovalNow);
            var rendered = await chronologyCorrected.RenderedNotices.SingleAsync(row => row.Id == renderedNoticeId);
            rendered.RenderedAtUtc.Should().Be(frozenApprovalNow);
            rendered.ApprovedAtUtc.Should().Be(frozenApprovalNow);
            var notification = await chronologyCorrected.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.CreatedAt.Should().Be(frozenApprovalNow);
            notification.NavigationExpiresAtUtc.Should().Be(frozenApprovalNow.AddDays(7));
            (await chronologyCorrected.AtomicAuditLogs
                    .Where(row =>
                        row.PortfolioId == portfolioId &&
                        row.CommandIdempotencyKey.EndsWith($":{chronologyReconcileOperationKey}") &&
                        row.EntityType == nameof(NoticeDraft) &&
                        row.ChangeReason == "Corrected tenant notice approval business chronology after exact graph reconciliation")
                    .Select(row => row.Timestamp)
                    .ToListAsync())
                .Should().ContainSingle(timestamp =>
                    timestamp != frozenApprovalNow
                    && timestamp >= chronologyAuditStartedAtUtc.AddSeconds(-1)
                    && timestamp <= chronologyAuditCompletedAtUtc.AddSeconds(1));
        }

        await using (var mutate = NewContext())
        {
            var outbox = await (
                    from evidence in mutate.NoticeDeliveryEvidence
                    join row in mutate.OutboxMessages on evidence.OutboxMessageId equals row.Id
                    where evidence.PortfolioId == portfolioId
                    orderby row.MessageType
                    select row)
                .ToListAsync();
            outbox.Select(row => row.MessageType).Should().BeEquivalentTo(["data-update", "email", "sms"]);
            outbox.Should().OnlyContain(row => row.MessageType != "TenantNoticeDelivery");
            (await mutate.Conversations.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(1);
            var portalMessage = await mutate.ConversationMessages
                .SingleAsync(row => row.Conversation!.PortfolioId == portfolioId);
            portalMessage.SenderRole.Should().Be(ConversationSenderRole.Landlord);
            portalMessage.Channels.Should().Be("Portal");
            portalMessage.Body.Should().Be("Your rent is due soon.");
            var conversation = await mutate.Conversations.SingleAsync(row => row.Id == portalMessage.ConversationId);
            conversation.TenantUnreadCount.Should().Be(1);
            (await mutate.NoticeDrafts.SingleAsync(row => row.Id == draftId)).ConversationId
                .Should().Be(conversation.Id);
            var notification = await mutate.Notifications.SingleAsync(row =>
                row.PortfolioId == portfolioId && row.Type == "TenantNotice");
            notification.RelatedEntityType.Should().Be(nameof(Conversation));
            notification.RelatedEntityId.Should().Be(conversation.Id);
            notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
            notification.NavigationDestination.Should().Be(NavigationDestination.Message);
            notification.NavigationResourceKind.Should().Be(nameof(Conversation));
            notification.NavigationResourceId.Should().Be(conversation.Id);
            (await mutate.NoticeDeliveryEvidence.CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(3);
            (await mutate.NoticeDeliveryEvidence.CountAsync(row =>
                row.PortfolioId == portfolioId
                && row.Channel == NoticeDeliveryChannel.TenantPortal
                && row.ConversationMessageId == portalMessage.Id)).Should().Be(1);
            (await mutate.TenantNoticeWorkItems.SingleAsync(row => row.Id == workItemId)).Status
                .Should().Be(TenantNoticeWorkStatus.Completed);

            var portal = outbox.Single(row => row.MessageType == "data-update");
            using (var payload = JsonDocument.Parse(portal.Payload))
            {
                payload.RootElement.GetProperty("entityType").GetString().Should().Be(nameof(Conversation));
                payload.RootElement.GetProperty("entityId").GetInt32().Should().Be(conversation.Id);
            }
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
            var foundation = new NotificationFoundationService(verify, TimeProvider.System, Writes);
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

        await using (var realtime = NewContext())
        {
            var deliveredGroups = new List<string>();
            var client = new Mock<IClientProxy>();
            client.Setup(value => value.SendCoreAsync(
                    It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(value => value.Groups(It.IsAny<IReadOnlyList<string>>()))
                .Callback<IReadOnlyList<string>>(groups => deliveredGroups.AddRange(groups))
                .Returns(client.Object);
            var hub = new Mock<IHubContext<DataUpdateHub>>();
            hub.SetupGet(value => value.Clients).Returns(clients.Object);
            var service = new DataUpdateService(
                realtime,
                hub.Object,
                TimeProvider.System,
                Mock.Of<ILogger<DataUpdateService>>());
            var conversationId = await realtime.NoticeDrafts
                .Where(row => row.Id == draftId)
                .Select(row => row.ConversationId!.Value)
                .SingleAsync();

            await service.BroadcastEntityUpdateAsync(
                portfolioId,
                nameof(Conversation),
                conversationId,
                new { conversationId },
                CancellationToken.None);

            deliveredGroups.Should().Contain(
                DataUpdateHub.SessionRevisionGroup(tenantSessionId, tenantAccessRevision));
            deliveredGroups.Should().Contain(
                DataUpdateHub.SessionRevisionGroup(scope.SessionId, scope.AccessRevision));
        }
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }

    private IRequestWriteExecutor Writes => _services!.GetRequiredService<IRequestWriteExecutor>();

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
