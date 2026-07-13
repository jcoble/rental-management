using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Sandbox guard + parameterized seeder coverage:
///   * <see cref="SandboxGuard"/> — the Stripe money-moving guard: true only for a sandbox portfolio;
///     false for live/unknown/null.
///   * <see cref="DemoDataSeeder.SeedPortfolioAsync"/> — seeds an ARBITRARY portfolio id and never
///     touches the sandbox flag, so the existing dev/e2e portfolio stays Live.
///   * Stripe checkout is suppressed (NotEnabled, no Stripe call) while a portfolio is sandbox.
/// </summary>
public class SandboxGuardAndSeederTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public SandboxGuardAndSeederTests()
    {
        var now = DateTime.UtcNow;
        var actor = new ApplicationUser
        {
            Id = 1,
            UserName = "sandbox-tests@example.test",
            NormalizedUserName = "SANDBOX-TESTS@EXAMPLE.TEST",
            Email = "sandbox-tests@example.test",
            NormalizedEmail = "SANDBOX-TESTS@EXAMPLE.TEST",
            DisplayName = "Sandbox Test Actor",
            CreatedAt = now,
        };
        _ctx.Db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            User = actor,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Membership = new WorkspaceMembership
            {
                PortfolioId = 1,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Management,
                EffectiveFromUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
        });
        _ctx.Db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------
    // SandboxGuard (the Stripe money-moving predicate)

    [Fact]
    public async Task SandboxGuard_True_WhenPortfolioIsSandbox()
    {
        var p = _ctx.Db.Portfolios.Single(x => x.Id == 1);
        p.IsSandbox = true;
        _ctx.Db.SaveChanges();

        var guard = new SandboxGuard(_ctx.Db);
        (await guard.IsSandboxAsync(1, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SandboxGuard_False_WhenLive_Unknown_OrNull()
    {
        // Portfolio 1 is Live by default.
        var guard = new SandboxGuard(_ctx.Db);

        (await guard.IsSandboxAsync(1, CancellationToken.None)).Should().BeFalse();   // live
        (await guard.IsSandboxAsync(999, CancellationToken.None)).Should().BeFalse(); // unknown
        (await guard.IsSandboxAsync(null, CancellationToken.None)).Should().BeFalse(); // unscoped/system
        (await guard.IsSandboxAsync(0, CancellationToken.None)).Should().BeFalse();    // zero id
    }

    // -----------------------------------------------------------------------
    // Parameterized seeder

    [Fact]
    public async Task SeedPortfolio_SeedsArbitraryPortfolio_WithoutTouchingSandboxFlag()
    {
        // A second portfolio (id 2), distinct from the pre-seeded anchor (id 1).
        var portfolio = new Portfolio
        {
            Id = 2,
            Name = "New Signup",
            ManagementCompanyName = "Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var accessContext = new WorkspaceAccessContext
        {
            UserId = _ctx.Db.Users.OrderBy(user => user.Id).Select(user => user.Id).First(),
            Portfolio = portfolio,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Membership = new WorkspaceMembership
            {
                PortfolioId = 2,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Management,
                EffectiveFromUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            },
        };
        _ctx.Db.Portfolios.Add(portfolio);
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.SaveChanges();

        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(_ctx.Db));
        await seeder.SeedPortfolioAsync(2, CancellationToken.None);

        // Demo data landed under portfolio 2, all FK'd correctly.
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.LeaseManagements.CountAsync(l => l.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.Tenants.IgnoreQueryFilters().CountAsync(t => t.PortfolioId == 2)).Should().BeGreaterThan(0);

        // Scan-persistence demo data: a subset of expenses carry the typed scan columns + child line
        // items. Canonical lease demo data carries explicit relationship/agreement/ledger facts;
        // it does not manufacture legacy Lease or Payment scan targets or fake legal-document bytes.
        var scannedExpenses = await _ctx.Db.Expenses.IgnoreQueryFilters()
            .Include(e => e.LineItems)
            .Where(e => e.PortfolioId == 2 && e.DocumentKind != null)
            .ToListAsync();
        scannedExpenses.Should().NotBeEmpty();
        scannedExpenses.Should().OnlyContain(e => e.ReceiptData != null && e.Subtotal != null && e.TaxAmount != null);
        scannedExpenses.Should().Contain(e => e.LineItems.Count > 0);
        scannedExpenses.Should().Contain(e => e.CardLast4 != null && e.PaymentMethod != null); // a card receipt
        scannedExpenses.Should().Contain(e => e.DocumentKind == "Invoice");                     // a vendor invoice

        (await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
            agreement.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.LeaseManagements.CountAsync(relationship =>
            relationship.PortfolioId == 2
            && relationship.PossessionAgreementExceptionReason != null)).Should().BeGreaterThan(0);
        (await _ctx.Db.WorkOrders.IgnoreQueryFilters()
            .CountAsync(w => w.PortfolioId == 2 && w.ExtractedData != null)).Should().BeGreaterThan(0);

        // The seeder does NOT touch the sandbox flag — that is the caller's (AuthService) responsibility.
        (await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 2)).IsSandbox.Should().BeFalse();

        // The pre-existing dev/e2e portfolio 1 is completely unaffected (no demo data, still Live).
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(0);
        (await _ctx.Db.Portfolios.SingleAsync(p => p.Id == 1)).IsSandbox.Should().BeFalse();
    }

    [Fact]
    public async Task SeedPortfolio_IsIdempotent()
    {
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(_ctx.Db));
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);
        var firstCount = await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1);
        firstCount.Should().BeGreaterThan(0);

        // Re-seeding is a no-op (the idempotency guard sees existing properties).
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(firstCount);
    }

    // -----------------------------------------------------------------------
    // Outbound guard: Stripe checkout suppressed while sandbox

    [Fact]
    public async Task StripeCheckout_Suppressed_WhenPortfolioIsSandbox()
    {
        // Stripe is ENABLED, ownership is valid — only the sandbox guard can short-circuit here.
        var (account, charge) = SeedTenantAccountAndRentCharge(portfolioId: 1, tenantId: 10);
        var p = _ctx.Db.Portfolios.Single(x => x.Id == 1);
        p.IsSandbox = true;
        _ctx.Db.SaveChanges();

        var sut = BuildStripeService(enabled: true);

        var result = await sut.CreatePaymentCheckoutSessionAsync(
            portfolioId: 1, tenantId: 10, tenantAccountId: account.Id,
            chargeLedgerEntryId: charge.Id, actorUserId: 1,
            successUrl: null, cancelUrl: null, CancellationToken.None);

        // Suppressed: returns NotEnabled WITHOUT contacting Stripe or creating a transaction.
        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
        _ctx.Db.TenantPaymentAttempts.Should().BeEmpty();
    }

    [Fact]
    public async Task StripeAutopay_Suppressed_WhenPortfolioIsSandbox()
    {
        var (account, _) = SeedTenantAccountAndRentCharge(portfolioId: 1, tenantId: 10);
        var p = _ctx.Db.Portfolios.Single(x => x.Id == 1);
        p.IsSandbox = true;
        _ctx.Db.SaveChanges();

        var sut = BuildStripeService(enabled: true);

        var result = await sut.CreateAutopaySetupSessionAsync(
            portfolioId: 1, tenantId: 10, tenantAccountId: account.Id, actorUserId: 1,
            operationKey: "sandbox-setup",
            successUrl: null, cancelUrl: null, CancellationToken.None);

        result.Result.Should().Be(CheckoutResult.Outcome.NotEnabled);
    }

    // -----------------------------------------------------------------------
    // Helpers

    private StripePaymentService BuildStripeService(bool enabled)
    {
        var config = new StripeConfig
        {
            SecretKey = enabled ? "sk_test_fake" : null,
            PublishableKey = enabled ? "pk_test_fake" : null,
            WebhookSecret = "whsec_test_secret",
        };

        return new StripePaymentService(
            Options.Create(config),
            new SandboxGuard(_ctx.Db),
            NullLogger<StripePaymentService>.Instance,
            TimeProvider.System,
            new UnexpectedAtomicUnitOfWork());
    }

    private (TenantAccount account, TenantLedgerEntry charge) SeedTenantAccountAndRentCharge(
        int portfolioId,
        int tenantId,
        decimal amount = 1000m)
    {
        var now = DateTime.UtcNow;

        var actor = _ctx.Db.Users.Single(user => user.Id == 1);

        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = "P",
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
            UnitNumber = $"U{tenantId}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            Id = tenantId,
            PortfolioId = portfolioId,
            FirstName = "T",
            LastName = tenantId.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{tenantId}",
            PossessionGivenAtUtc = now.AddMonths(-6),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
            RowVersion = Guid.NewGuid(),
        };
        _ctx.Db.LeaseManagements.Add(relationship);
        _ctx.Db.SaveChanges();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{tenantId}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = $"AGR-{tenantId}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-6)),
            TermEndOn = DateOnly.FromDateTime(now.AddMonths(6)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-6)),
            BaseRentAmount = amount,
            RentDueDay = 1,
            SecurityDepositObligation = amount,
            LateFeeAmount = 0m,
            GracePeriodDays = 0,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                portfolioId, actor.Id, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = tenantId,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-6)),
            ChangeReason = "Canonical sandbox payment fixture",
            CreatedAtUtc = now,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.AddRange(account, agreement, party);
        _ctx.Db.SaveChanges();

        var charge = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Scheduled rent charge",
            BusinessKey = $"rent:{now:yyyy-MM}:{tenantId}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = actor.Id,
        };
        _ctx.Db.TenantLedgerEntries.Add(charge);
        _ctx.Db.SaveChanges();

        return (account, charge);
    }
}
