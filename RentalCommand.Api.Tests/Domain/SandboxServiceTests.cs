using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Go-live (graduate-once → wipe demo) coverage for <see cref="SandboxService"/>: a sandbox portfolio's
/// data is wiped and the flag flipped to Live; the operation is idempotent; and it only ever affects the
/// caller's own portfolio (IDOR-safe) — a second, sandboxed portfolio is left fully intact. Also pins
/// C-4: after the wipe the new Live portfolio is re-seeded with the landlord's own primary self-owner.
/// </summary>
public class SandboxServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private SandboxService BuildService()
    {
        var provisioner = new SelfOwnerProvisioner(_ctx.Db, NullLogger<SelfOwnerProvisioner>.Instance, TimeProvider.System);
        var seeder = new RentalCommand.Api.Services.Auth.DemoDataSeeder(
            _ctx.Db, NullLogger<RentalCommand.Api.Services.Auth.DemoDataSeeder>.Instance, TimeProvider.System);
        return new SandboxService(_ctx.Db, provisioner, seeder, NullLogger<SandboxService>.Instance, TimeProvider.System);
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
    public async Task GoLive_ForMissingPortfolio_ReturnsNull()
    {
        var state = await BuildService().GoLiveAsync(999, CancellationToken.None);
        state.Should().BeNull();
    }

    [Fact]
    public async Task GoLive_CreatesPrimarySelfOwner_FromTheAdminUser_AfterWipingDemoOwners()
    {
        // C-4: the wipe removes the demo owners, so the fresh Live portfolio must be re-seeded with the
        // landlord's own primary owner — otherwise the getting-started "owner" step blocks "add property".
        MarkSandbox(portfolioId: 1, DateTime.UtcNow);
        SeedRichGraph(portfolioId: 1, actorDisplayName: "Pat Owner", actorEmail: "owner@example.com");
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
        await BuildService().GoLiveAsync(1, CancellationToken.None);

        // Exactly one owner remains: the primary self-owner derived from the account.
        var owners = await _ctx.Db.OwnerEntities.IgnoreQueryFilters()
            .Where(o => o.PortfolioId == 1).ToListAsync();
        owners.Should().ContainSingle();
        owners[0].IsPrimary.Should().BeTrue();
        owners[0].Name.Should().Be("Pat Owner");
        owners[0].Email.Should().Be("owner@example.com");
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
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);

        var state = await BuildService()
            .ApplyOnboardingChoiceAsync(1, OnboardingChoice.Sandbox, CancellationToken.None);

        state.Should().NotBeNull();
        state!.IsSandbox.Should().BeTrue();
        state.SandboxSeededAtUtc.Should().NotBeNull();
        state.OnboardingChoicePending.Should().BeFalse();

        // Demo data was actually seeded.
        (await _ctx.Db.Properties.CountAsync()).Should().BeGreaterThan(0);

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
            PortfolioId = portfolioId,
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
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.LeaseAgreements.Add(agreement);
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
            RecipientTenantId = tenant.Id,
            PropertyId = property.Id,
            NoticeType = "RentReminder",
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
