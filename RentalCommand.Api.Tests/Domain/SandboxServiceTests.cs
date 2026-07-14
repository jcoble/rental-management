using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Go-live (graduate-once → wipe demo) coverage for <see cref="SandboxService"/>: a sandbox portfolio's
/// data is wiped and the flag flipped to Live; the operation is idempotent; and it only ever affects the
/// caller's own portfolio (IDOR-safe) — a second, sandboxed portfolio is left fully intact. Also pins
/// C-4: the canonical primary owner and its relationship survive the demo-data wipe.
/// </summary>
public class SandboxServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private SandboxService BuildService()
    {
        var seeder = new RentalCommand.Api.Services.Auth.DemoDataSeeder(
            _ctx.Db,
            NullLogger<RentalCommand.Api.Services.Auth.DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(_ctx.Db));
        return new SandboxService(
            _ctx.Db, seeder, NullLogger<SandboxService>.Instance, TimeProvider.System);
    }

    // -----------------------------------------------------------------------
    // State read

    [Fact]
    public async Task GetState_ReturnsSandboxFlagAndSeededAt()
    {
        var seededAt = DateTime.UtcNow.AddMinutes(-5);
        MarkSandbox(portfolioId: 1, seededAt);

        var state = await BuildService().GetStateAsync(1, CancellationToken.None);

        state.Should().NotBeNull();
        state!.PortfolioId.Should().Be(1);
        state.IsSandbox.Should().BeTrue();
        state.SandboxSeededAtUtc.Should().BeCloseTo(seededAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetState_ForMissingPortfolio_ReturnsNull()
    {
        var state = await BuildService().GetStateAsync(999, CancellationToken.None);
        state.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Go-live: wipe + flip

    [Fact]
    public async Task GoLive_WipesDomainData_AndFlipsToLive()
    {
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1);

        // Sanity: data is present before go-live.
        (await _ctx.Db.Properties.CountAsync()).Should().BeGreaterThan(0);
        (await _ctx.Db.LeaseManagements.CountAsync()).Should().BeGreaterThan(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync()).Should().BeGreaterThan(0);
        (await _ctx.Db.Conversations.CountAsync()).Should().BeGreaterThan(0);

        var state = await BuildService().GoLiveAsync(1, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeFalse();
        state.SandboxSeededAtUtc.Should().BeNull();

        // The flag flipped...
        var portfolio = await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 1);
        portfolio.IsSandbox.Should().BeFalse();
        portfolio.SandboxSeededAtUtc.Should().BeNull();

        // ...and every portfolio-scoped row was wiped (the Portfolio row itself survives).
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.Units.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.LeaseManagements.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.LeaseAgreements.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.LeaseAgreementSigners.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.LegalDocumentArtifacts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.SignatureRequests.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.SignatureSigners.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.SignatureAuditEvents.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.StoredFiles.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.TenantAccounts.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.TenantLedgerEntries.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.Expenses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.Tenants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.Vendors.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.WorkOrders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _ctx.Db.Conversations.CountAsync()).Should().Be(0);
        (await _ctx.Db.ConversationMessages.CountAsync()).Should().Be(0);
        (await _ctx.Db.NoticeDrafts.CountAsync()).Should().Be(0);
        (await _ctx.Db.Portfolios.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GoLive_IsIdempotent_OnAlreadyLivePortfolio()
    {
        // Portfolio 1 starts Live (default). Seed a couple of rows that must NOT be touched.
        SeedRichGraph(portfolioId: 1);
        var propsBefore = await _ctx.Db.Properties.CountAsync();

        var state = await BuildService().GoLiveAsync(1, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeFalse();
        // No-op on an already-live account: data is left intact (we did not wipe a real portfolio).
        (await _ctx.Db.Properties.CountAsync()).Should().Be(propsBefore);
    }

    [Fact]
    public async Task GoLive_PreservesIdentityAndUserTemplates_WithTheirFiles()
    {
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1);
        var now = DateTime.UtcNow;

        var userTemplateFile = new StoredFile
        {
            PortfolioId = 1,
            FileName = "landlord-lease.pdf",
            FilePath = "templates/landlord-lease.pdf",
            ContentType = "application/pdf",
            FileSize = 25,
            EntityType = nameof(DocumentTemplate),
            UploadedAt = now,
        };
        var sandboxTemplateFile = new StoredFile
        {
            PortfolioId = 1,
            FileName = "demo-lease.pdf",
            FilePath = "templates/demo-lease.pdf",
            ContentType = "application/pdf",
            FileSize = 20,
            EntityType = nameof(DocumentTemplate),
            UploadedAt = now,
        };
        var userTemplate = NewTemplate("Landlord lease", userTemplateFile, isSandboxSeeded: false, now);
        var sandboxTemplate = NewTemplate("Demo lease", sandboxTemplateFile, isSandboxSeeded: true, now);
        _ctx.Db.DocumentTemplates.AddRange(userTemplate, sandboxTemplate);
        var sandboxPropertyId = await _ctx.Db.Properties.Select(property => property.Id).FirstAsync();
        _ctx.Db.TeamRoutingRules.AddRange(
            new TeamRoutingRule
            {
                PortfolioId = 1,
                Topic = TeamRoutingTopic.WorkOrders,
                PropertyId = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
            new TeamRoutingRule
            {
                PortfolioId = 1,
                Topic = TeamRoutingTopic.ApplicationsAndLeasing,
                PropertyId = sandboxPropertyId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        await _ctx.Db.SaveChangesAsync();

        var accountingConnection = new AccountingConnection
        {
            PortfolioId = 1,
            Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AccountingConnections.Add(accountingConnection);
        await _ctx.Db.SaveChangesAsync();
        var sandboxTenantId = await _ctx.Db.Tenants.Select(tenant => tenant.Id).FirstAsync();
        var sandboxLedgerEntryId = await _ctx.Db.TenantLedgerEntries.Select(entry => entry.Id).FirstAsync();
        _ctx.Db.AccountingEntityMappings.AddRange(
            new AccountingEntityMapping
            {
                PortfolioId = 1,
                AccountingConnectionId = accountingConnection.Id,
                LocalEntityType = "ScheduleECategory",
                LocalEnumValue = "Repairs",
                ExternalType = "Account",
                ExternalId = "enum-account",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new AccountingEntityMapping
            {
                PortfolioId = 1,
                AccountingConnectionId = accountingConnection.Id,
                LocalEntityType = "Tenant",
                LocalEntityId = sandboxTenantId,
                ExternalType = "Customer",
                ExternalId = "sandbox-tenant",
                CreatedAt = now,
                UpdatedAt = now,
            });
        _ctx.Db.AccountingSyncMaps.AddRange(
            new AccountingSyncMap
            {
                PortfolioId = 1,
                AccountingConnectionId = accountingConnection.Id,
                Direction = "Import",
                ExternalType = "Payment",
                ExternalId = "parked-payment",
                Status = "NeedsReview",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new AccountingSyncMap
            {
                PortfolioId = 1,
                AccountingConnectionId = accountingConnection.Id,
                Direction = "Import",
                ExternalType = "Payment",
                ExternalId = "sandbox-payment",
                LocalEntityType = "TenantLedgerEntry",
                LocalEntityId = sandboxLedgerEntryId,
                Status = "Imported",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _ctx.Db.SaveChangesAsync();

        await BuildService().GoLiveAsync(1, CancellationToken.None);

        (await _ctx.Db.DocumentTemplates.IgnoreQueryFilters().Select(template => template.Name).ToListAsync())
            .Should().Equal("Landlord lease");
        (await _ctx.Db.DocumentTemplateFields.IgnoreQueryFilters().Select(field => field.FieldKey).ToListAsync())
            .Should().Equal("tenant.fullName");
        (await _ctx.Db.StoredFiles.IgnoreQueryFilters().Select(file => file.FileName).ToListAsync())
            .Should().Equal("landlord-lease.pdf");
        (await _ctx.Db.TeamRoutingRules.IgnoreQueryFilters().Select(rule => new { rule.Topic, rule.PropertyId }).ToListAsync())
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Topic = TeamRoutingTopic.WorkOrders, PropertyId = (int?)null });
        (await _ctx.Db.AccountingConnections.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await _ctx.Db.AccountingEntityMappings.IgnoreQueryFilters().Select(mapping => mapping.LocalEnumValue).ToListAsync())
            .Should().Equal("Repairs");
        (await _ctx.Db.AccountingSyncMaps.IgnoreQueryFilters().Select(mapping => mapping.ExternalId).ToListAsync())
            .Should().Equal("parked-payment");
    }

    [Fact]
    public async Task GoLive_CompletesWithoutAnApplicationBypassContext()
    {
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1);

        await BuildService().GoLiveAsync(1, CancellationToken.None);
        (await _ctx.Db.Portfolios.SingleAsync(portfolio => portfolio.Id == 1)).IsSandbox
            .Should().BeFalse();
    }

    [Fact]
    public async Task GoLive_ForMissingPortfolio_ReturnsNull()
    {
        var state = await BuildService().GoLiveAsync(999, CancellationToken.None);
        state.Should().BeNull();
    }

    [Fact]
    public async Task GoLive_PreservesCanonicalPrimaryOwner_WhileWipingDemoOwners()
    {
        // Registration creates this canonical owner and relationship atomically. Sandbox graduation
        // preserves them rather than deleting and recreating the same authority graph through a second path.
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1, actorDisplayName: "Pat Owner", actorEmail: "owner@example.com");
        var user = await _ctx.Db.Users.SingleAsync(candidate => candidate.Email == "owner@example.com");
        var context = await _ctx.Db.WorkspaceAccessContexts.SingleAsync(candidate =>
            candidate.UserId == user.Id && candidate.PortfolioId == 1);
        var primary = new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Pat Owner",
            Email = "owner@example.com",
            IsPrimary = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.OwnerUserAccesses.Add(new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            ApplicationUser = user,
            AccessContext = context,
            OwnerEntity = primary,
            EffectiveFromUtc = DateTime.UtcNow,
            GrantedAtUtc = DateTime.UtcNow,
            GrantedByUser = user,
            Reason = "Initial workspace owner relationship",
        });
        // Demo owner that the wipe should remove.
        _ctx.Db.OwnerEntities.Add(new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.LLC,
            Name = "Demo Holdings LLC",
            IsPrimary = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();
        await BuildService().GoLiveAsync(1, CancellationToken.None);

        // Exactly one owner remains: the original canonical primary owner and its access relationship.
        _ctx.Db.ChangeTracker.Clear();
        var owners = await _ctx.Db.OwnerEntities.IgnoreQueryFilters()
            .Where(o => o.PortfolioId == 1).ToListAsync();
        owners.Should().ContainSingle();
        owners[0].IsPrimary.Should().BeTrue();
        owners[0].Name.Should().Be("Pat Owner");
        owners[0].Email.Should().Be("owner@example.com");
        (await _ctx.Db.OwnerUserAccesses.CountAsync(access =>
            access.PortfolioId == 1 && access.OwnerEntityId == owners[0].Id)).Should().Be(1);
    }

    [Fact]
    public async Task GoLive_OnlyAffectsCallersOwnPortfolio()
    {
        // Two sandbox portfolios, each with its own data.
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1);

        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            IsSandbox = true,
            SandboxSeededAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
        SeedRichGraph(portfolioId: 2);

        var p2PropsBefore = await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 2);
        p2PropsBefore.Should().BeGreaterThan(0);

        // Graduate ONLY portfolio 1.
        await BuildService().GoLiveAsync(1, CancellationToken.None);

        // Portfolio 1 wiped + Live.
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(0);
        (await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 1)).IsSandbox.Should().BeFalse();

        // Portfolio 2 entirely untouched — still sandbox, still has all its data.
        var p2 = await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 2);
        p2.IsSandbox.Should().BeTrue();
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 2)).Should().Be(p2PropsBefore);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.Conversations.CountAsync(c => c.PortfolioId == 2)).Should().BeGreaterThan(0);
    }

    // -----------------------------------------------------------------------
    // First-login onboarding choice (Sandbox vs Live)

    [Fact]
    public async Task GetState_OnFreshPortfolio_ReportsOnboardingChoicePending()
    {
        // Portfolio 1 has null Settings → no choice recorded yet → the gate must fire.
        var state = await BuildService().GetStateAsync(1, CancellationToken.None);

        state.Should().NotBeNull();
        state!.OnboardingChoicePending.Should().BeTrue();
    }

    [Fact]
    public async Task OnboardingChoice_Sandbox_SeedsDemoData_AndFlipsToSandbox()
    {
        SeedAdministeringUser(portfolioId: 1);
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);

        var state = await BuildService()
            .ApplyOnboardingChoiceAsync(1, OnboardingChoice.Sandbox, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeTrue();
        state.SandboxSeededAtUtc.Should().NotBeNull();
        state.OnboardingChoicePending.Should().BeFalse();

        // Demo data was actually seeded.
        (await _ctx.Db.Properties.CountAsync()).Should().BeGreaterThan(0);
        (await _ctx.Db.DocumentTemplates.SingleAsync()).IsSandboxSeeded.Should().BeTrue();

        var portfolio = await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 1);
        portfolio.IsSandbox.Should().BeTrue();
    }

    [Fact]
    public async Task OnboardingChoice_Live_LeavesPortfolioEmpty_AndLive()
    {
        var state = await BuildService()
            .ApplyOnboardingChoiceAsync(1, OnboardingChoice.Live, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeFalse();
        state.SandboxSeededAtUtc.Should().BeNull();
        state.OnboardingChoicePending.Should().BeFalse();

        // No demo data was seeded — a real, empty portfolio.
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);
        (await _ctx.Db.Tenants.CountAsync()).Should().Be(0);

        var portfolio = await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 1);
        portfolio.IsSandbox.Should().BeFalse();
    }

    [Fact]
    public async Task OnboardingChoice_IsIdempotent_SecondCallDoesNotReSeedOrWipe()
    {
        SeedAdministeringUser(portfolioId: 1);
        var svc = BuildService();
        await svc.ApplyOnboardingChoiceAsync(1, OnboardingChoice.Sandbox, CancellationToken.None);
        var seededCount = await _ctx.Db.Properties.CountAsync();
        seededCount.Should().BeGreaterThan(0);

        // A second, conflicting choice must be a no-op: the recorded Sandbox state stands, data intact.
        var state = await svc.ApplyOnboardingChoiceAsync(1, OnboardingChoice.Live, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeTrue();
        state.OnboardingChoicePending.Should().BeFalse();
        (await _ctx.Db.Properties.CountAsync()).Should().Be(seededCount);
    }

    [Fact]
    public async Task OnboardingChoice_ForMissingPortfolio_ReturnsNull()
    {
        var state = await BuildService()
            .ApplyOnboardingChoiceAsync(999, OnboardingChoice.Sandbox, CancellationToken.None);
        state.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Helpers

    private void MarkSandbox(int portfolioId, DateTime seededAt)
    {
        var p = _ctx.Db.Portfolios.Single(x => x.Id == portfolioId);
        p.IsSandbox = true;
        p.SandboxSeededAtUtc = seededAt;
        _ctx.Db.SaveChanges();
    }

    private void SeedAdministeringUser(int portfolioId)
    {
        var now = DateTime.UtcNow;
        var actor = new ApplicationUser
        {
            UserName = $"onboarding-{portfolioId}@example.test",
            Email = $"onboarding-{portfolioId}@example.test",
            DisplayName = "Onboarding Administrator",
            EmailConfirmed = true,
            CreatedAt = now,
        };
        _ctx.Db.Users.Add(actor);
        _ctx.Db.SaveChanges();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.SaveChanges();
        _ctx.Db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        _ctx.Db.SaveChanges();
    }

    private static DocumentTemplate NewTemplate(
        string name,
        StoredFile file,
        bool isSandboxSeeded,
        DateTime now)
    {
        var template = new DocumentTemplate
        {
            PortfolioId = 1,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = name,
            OriginalStoredFile = file,
            IsSandboxSeeded = isSandboxSeeded,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        template.Fields.Add(new DocumentTemplateField
        {
            FieldKey = isSandboxSeeded ? "demo.tenantName" : "tenant.fullName",
            Label = "Tenant full name",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.1,
            YPct = 0.1,
            WidthPct = 0.4,
            HeightPct = 0.05,
        });
        return template;
    }

    /// <summary>
    /// Seeds a small but FK-rich graph for a portfolio so the wipe ordering is exercised across the
    /// tricky constraints: NoticeDraft→canonical account/ledger/relationship (RESTRICT),
    /// Conversation→Tenant (RESTRICT), Unit (no PortfolioId), ConversationMessage (no PortfolioId),
    /// and the work-order/expense chain.
    /// </summary>
    private void SeedRichGraph(
        int portfolioId,
        string actorDisplayName = "Sandbox Fixture",
        string? actorEmail = null)
    {
        var now = DateTime.UtcNow;
        actorEmail ??= $"sandbox-{portfolioId}@example.test";

        var actor = new ApplicationUser
        {
            UserName = actorEmail,
            Email = actorEmail,
            DisplayName = actorDisplayName,
            EmailConfirmed = true,
            CreatedAt = now,
        };
        _ctx.Db.Users.Add(actor);
        _ctx.Db.SaveChanges();

        var accessContext = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.SaveChanges();
        _ctx.Db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            AccessContextId = accessContext.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        // Ensure the portfolio exists (portfolio 1 is pre-seeded; others are added by the caller).
        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = $"P{portfolioId}",
            AddressLine1 = "1 St",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit
        {
            PortfolioId = portfolioId,
            Property = property,
            UnitNumber = $"U{portfolioId}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = portfolioId,
            FirstName = "T",
            LastName = portfolioId.ToString(),
            Email = $"tenant-{portfolioId}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);

        var vendor = new Vendor
        {
            PortfolioId = portfolioId,
            Name = "V",
            ServiceType = "Plumbing",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Vendors.Add(vendor);
        _ctx.Db.SaveChanges();

        var relationship = new LeaseManagement
        {
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            PublicId = Guid.NewGuid(),
            RelationshipNumber = $"LM-{portfolioId}",
            PlannedPossessionAtUtc = now.AddMonths(-6),
            PossessionGivenAtUtc = now.AddMonths(-6),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(relationship);
        _ctx.Db.SaveChanges();

        var party = new LeaseManagementParty
        {
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-6)),
            ChangeReason = "Sandbox canonical fixture",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{portfolioId}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-6),
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.AddRange(party, account);
        _ctx.Db.SaveChanges();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{portfolioId}-V1",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-6)),
            TermEndOn = DateOnly.FromDateTime(now.AddMonths(6)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-6)),
            BaseRentAmount = 1000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                portfolioId, actor.Id, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.LeaseAgreements.Add(agreement);
        _ctx.Db.SaveChanges();

        var agreementSigner = new LeaseAgreementSigner
        {
            PortfolioId = portfolioId,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = party.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = $"{tenant.FirstName} {tenant.LastName}",
            EmailSnapshot = tenant.Email!,
            SigningOrder = 1,
            IsRequired = true,
        };
        _ctx.Db.LeaseAgreementSigners.Add(agreementSigner);
        _ctx.Db.SaveChanges();

        var issuedFile = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = $"agreement-{portfolioId}-issued.pdf",
            FilePath = $"legal/{portfolioId}/issued.pdf",
            ContentType = "application/pdf",
            FileSize = 100,
            EntityType = nameof(LeaseAgreement),
            EntityId = agreement.Id,
            UploadedAt = now,
        };
        var executedFile = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = $"agreement-{portfolioId}-executed.pdf",
            FilePath = $"legal/{portfolioId}/executed.pdf",
            ContentType = "application/pdf",
            FileSize = 120,
            EntityType = nameof(LeaseAgreement),
            EntityId = agreement.Id,
            UploadedAt = now,
        };
        _ctx.Db.StoredFiles.AddRange(issuedFile, executedFile);
        _ctx.Db.SaveChanges();

        var issuedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            StoredFileId = issuedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = issuedFile.FilePath,
            FileName = issuedFile.FileName,
            ContentType = issuedFile.ContentType,
            ByteLength = issuedFile.FileSize,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var executedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            StoredFileId = executedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
            StorageKey = executedFile.FilePath,
            FileName = executedFile.FileName,
            ContentType = executedFile.ContentType,
            ByteLength = executedFile.FileSize,
            ContentSha256 = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _ctx.Db.SaveChanges();

        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = now;
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = now;
        agreement.UpdatedAtUtc = now;
        _ctx.Db.SaveChanges();

        var signatureRequest = new SignatureRequest
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseAgreementId = agreement.Id,
            Provider = "native",
            IdempotencyKey = $"sandbox-signature-{portfolioId}",
            Status = SignatureRequestStatus.Completed,
            Subject = "Sandbox executed agreement",
            IssuedArtifactId = issuedArtifact.Id,
            ExecutedArtifactId = executedArtifact.Id,
            PreparedAtUtc = now,
            CompletedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var signatureSigner = new SignatureSigner
        {
            PortfolioId = portfolioId,
            AgreementSignerId = agreementSigner.Id,
            NameSnapshot = agreementSigner.NameSnapshot,
            EmailSnapshot = agreementSigner.EmailSnapshot,
            SigningOrder = agreementSigner.SigningOrder,
            IsRequired = true,
            TokenHash = new string(portfolioId % 2 == 0 ? 'd' : 'c', 64),
            TokenExpiresAtUtc = now.AddDays(14),
            Status = SignatureSignerStatus.Signed,
            ConsentGivenAtUtc = now,
            ViewedAtUtc = now,
            SignedAtUtc = now,
            SignatureType = SignatureSignatureType.Typed,
            TypedName = agreementSigner.NameSnapshot,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        signatureRequest.Signers.Add(signatureSigner);
        signatureRequest.AuditEvents.Add(new SignatureAuditEvent
        {
            PortfolioId = portfolioId,
            SignatureSigner = signatureSigner,
            Type = SignatureAuditEventType.Completed,
            OccurredAtUtc = now,
            Detail = "Sandbox signed agreement fixture.",
        });
        _ctx.Db.SignatureRequests.Add(signatureRequest);
        _ctx.Db.SaveChanges();

        var rentCharge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1000m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Sandbox rent charge",
            BusinessKey = $"sandbox-rent:{portfolioId}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.TenantLedgerEntries.Add(rentCharge);
        _ctx.Db.SaveChanges();

        _ctx.Db.NoticeDrafts.Add(new NoticeDraft
        {
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            TenantAccountId = account.Id,
            TenantLedgerEntryId = rentCharge.Id,
            RecipientLeaseManagementPartyId = party.Id,
            LeaseAgreementId = agreement.Id,
            PropertyId = property.Id,
            NoticeType = "rent-reminder",
            Status = "Draft",
            Subject = "Rent reminder",
            Body = "Sandbox notice",
            Reason = "Sandbox canonical fixture",
            TriggerDate = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var workOrder = new WorkOrder
        {
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            VendorId = vendor.Id,
            Title = "Fix",
            Description = "desc",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.WorkOrders.Add(workOrder);
        _ctx.Db.SaveChanges();

        _ctx.Db.Expenses.Add(new Expense
        {
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            VendorId = vendor.Id,
            WorkOrderId = workOrder.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Repair",
            Amount = 100m,
            Status = ExpenseStatus.Paid,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _ctx.Db.WorkOrderStatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = portfolioId,
            WorkOrderId = workOrder.Id,
            FromStatus = WorkOrderStatus.New,
            ToStatus = WorkOrderStatus.InProgress,
            CreatedAtUtc = now,
        });

        // Conversation RESTRICTs Tenant; its messages have no PortfolioId (join-deleted).
        var conversation = new Conversation
        {
            PortfolioId = portfolioId,
            TenantId = tenant.Id,
            Subject = "Rent",
            CreatedAt = now,
            LastMessageAt = now,
        };
        _ctx.Db.Conversations.Add(conversation);
        _ctx.Db.SaveChanges();

        _ctx.Db.ConversationMessages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            SenderRole = ConversationSenderRole.Tenant,
            Body = "Hi",
            CreatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }
}
