using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
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

        var infrastructure = new TestAtomicInfrastructureUnitOfWork(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(_ctx.Db, infrastructure);
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(_ctx.Db),
            infrastructure,
            infrastructure,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments);
        await seeder.SeedPortfolioAsync(2, CancellationToken.None);

        // Demo data landed under portfolio 2, all FK'd correctly.
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.LeaseManagements.CountAsync(l => l.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.Tenants.IgnoreQueryFilters().CountAsync(t => t.PortfolioId == 2)).Should().BeGreaterThan(0);

        // Scan-persistence demo data: a subset of expenses carry the typed scan columns + child line
        // items. Canonical lease demo data carries explicit relationship/agreement/ledger facts and
        // rendered legal artifacts; it does not manufacture legacy Lease or Payment scan targets.
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
        var executedDemo = await _ctx.Db.LeaseAgreements
            .Include(agreement => agreement.IssuedArtifact)
            .Include(agreement => agreement.ExecutedArtifact)
            .Include(agreement => agreement.LeaseManagement)
            .SingleAsync(agreement => agreement.PortfolioId == 2
                && agreement.AgreementNumber == "DEMO-AGR-ACTIVE-001-V1");
        executedDemo.IssuedAtUtc.Should().NotBeNull();
        executedDemo.FullyExecutedAtUtc.Should().NotBeNull();
        executedDemo.IssuedArtifact.Should().NotBeNull();
        executedDemo.ExecutedArtifact.Should().NotBeNull();
        executedDemo.IssuedArtifact!.ByteLength.Should().BeGreaterThan(0);
        executedDemo.ExecutedArtifact!.ByteLength.Should().BeGreaterThan(0);
        executedDemo.LeaseManagement!.PossessionAgreementExceptionReason.Should().BeNull();

        var noAgreementDemo = await _ctx.Db.LeaseManagements
            .Include(relationship => relationship.Agreements)
            .Include(relationship => relationship.TenantAccount)
            .SingleAsync(relationship => relationship.PortfolioId == 2
                && relationship.RelationshipNumber == "DEMO-LM-ACTIVE-017");
        noAgreementDemo.TenantAccount!.AccountNumber.Should().Be("DEMO-TA-ACTIVE-017");
        noAgreementDemo.PossessionAgreementExceptionReason.Should().NotBeNullOrWhiteSpace();
        noAgreementDemo.Agreements.Should().NotContain(agreement =>
            agreement.FullyExecutedAtUtc != null && agreement.ExecutedArtifactId != null);

        var contradictoryCurrentRelationships = await _ctx.Db.LeaseManagements.CountAsync(relationship =>
            relationship.PortfolioId == 2
            && relationship.RelationshipNumber.StartsWith("DEMO-LM-")
            && relationship.PossessionGivenAtUtc != null
            && relationship.PossessionReturnedAtUtc == null
            && relationship.EndingDisposition == LeaseManagementEndingDisposition.Undecided
            && relationship.PlannedMoveOutAtUtc != null);
        contradictoryCurrentRelationships.Should().Be(0);
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
        var infrastructure = new TestAtomicInfrastructureUnitOfWork(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(_ctx.Db, infrastructure);
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(_ctx.Db),
            infrastructure,
            infrastructure,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments);
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);
        var firstCount = await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1);
        firstCount.Should().BeGreaterThan(0);

        // Re-seeding reconciles stable facts without duplicating the existing graph.
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(firstCount);
    }

    [Fact]
    public async Task SeedPortfolio_FailureBeforeFirstUpload_RetryCompletesWithoutPlaceholderOrDuplicates()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        legalDocuments.FailUploadAttempt = 1;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(1, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<IOException>();

        (await _ctx.Db.Properties.CountAsync()).Should().BeGreaterThan(0,
            "the canonical graph commits before provider storage is attempted");
        (await _ctx.Db.LegalDocumentArtifacts.CountAsync()).Should().Be(0);
        var failedAgreement = await _ctx.Db.LeaseAgreements
            .SingleAsync(agreement => agreement.AgreementNumber == "DEMO-AGR-ACTIVE-001-V1");
        failedAgreement.IssuedArtifactId.Should().BeNull();
        failedAgreement.ExecutedArtifactId.Should().BeNull();
        failedAgreement.FullyExecutedAtUtc.Should().BeNull();

        legalDocuments.FailUploadAttempt = null;
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);

        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_SecondUploadFailure_ReusesAdmissionsAndPathsWithoutDuplicates()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        legalDocuments.FailUploadAttempt = 2;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(1, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<IOException>();

        var firstAdmissions = legalDocuments.AdmissionsByPurpose.ToDictionary(pair => pair.Key, pair => pair.Value);
        firstAdmissions.Should().HaveCount(2);
        (await _ctx.Db.PendingFileUploads.CountAsync()).Should().Be(2);
        (await _ctx.Db.LegalDocumentArtifacts.CountAsync()).Should().Be(0);
        legalDocuments.StoredBytes.Should().ContainKey(firstAdmissions["demo-legal-issued"].StoragePath);
        legalDocuments.StoredBytes.Should().NotContainKey(firstAdmissions["demo-legal-executed"].StoragePath);

        legalDocuments.FailUploadAttempt = null;
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);

        legalDocuments.AdmissionsByPurpose.Should().BeEquivalentTo(firstAdmissions);
        (await _ctx.Db.PendingFileUploads.CountAsync()).Should().Be(2);
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_FinalizationFailure_RollsBackAllMetadataAndRetryCompletesOnce()
    {
        var failure = new FinalizationFailureInterceptor();
        using var context = new SqliteTestContext([failure]);
        SeedAdministeringAccess(context.Db);
        var (seeder, legalDocuments) = BuildSeeder(context.Db);
        failure.Armed = true;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(1, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InjectedFinalizationException>();

        context.Db.ChangeTracker.Clear();
        (await context.Db.StoredFiles.CountAsync()).Should().Be(0);
        (await context.Db.LegalDocumentArtifacts.CountAsync()).Should().Be(0);
        (await context.Db.PendingFileUploads.CountAsync(upload =>
            upload.State == PendingFileUploadState.Finalized)).Should().Be(0);
        var failedAgreement = await context.Db.LeaseAgreements
            .SingleAsync(agreement => agreement.AgreementNumber == "DEMO-AGR-ACTIVE-001-V1");
        failedAgreement.IssuedArtifactId.Should().BeNull();
        failedAgreement.ExecutedArtifactId.Should().BeNull();
        failedAgreement.FullyExecutedAtUtc.Should().BeNull();

        await seeder.SeedPortfolioAsync(1, CancellationToken.None);

        await AssertCanonicalArtifactsAsync(context.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_WithExistingProperties_ReconcilesCurrentFactsAndArtifacts()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        await seeder.SeedPortfolioAsync(1, CancellationToken.None);
        var relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.RelationshipNumber == "DEMO-LM-ACTIVE-017");
        relationship.PlannedMoveOutAtUtc = DateTime.UtcNow.AddMonths(1);
        relationship.EndingDispositionDecidedAtUtc = DateTime.UtcNow;
        relationship.EndingDispositionDecidedByUserId = 1;
        await _ctx.Db.SaveChangesAsync();

        await seeder.SeedPortfolioAsync(1, CancellationToken.None);

        _ctx.Db.ChangeTracker.Clear();
        relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.RelationshipNumber == "DEMO-LM-ACTIVE-017");
        relationship.PlannedMoveOutAtUtc.Should().BeNull();
        relationship.EndingDispositionDecidedAtUtc.Should().BeNull();
        relationship.EndingDispositionDecidedByUserId.Should().BeNull();
        relationship.PossessionAgreementExceptionReason.Should().NotBeNullOrWhiteSpace();
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
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

    private static (DemoDataSeeder Seeder, DemoLegalTestDependencies LegalDocuments) BuildSeeder(
        RentalCommandDbContext db)
    {
        var infrastructure = new TestAtomicInfrastructureUnitOfWork(db);
        var legalDocuments = new DemoLegalTestDependencies(db, infrastructure);
        return (new DemoDataSeeder(
            db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            new LegalDocumentSourceVersionTestResolver(db),
            infrastructure,
            infrastructure,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments), legalDocuments);
    }

    private static void SeedAdministeringAccess(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var actor = new ApplicationUser
        {
            UserName = "finalization-tests@example.test",
            NormalizedUserName = "FINALIZATION-TESTS@EXAMPLE.TEST",
            Email = "finalization-tests@example.test",
            NormalizedEmail = "FINALIZATION-TESTS@EXAMPLE.TEST",
            DisplayName = "Finalization Test Actor",
            CreatedAt = now,
        };
        db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
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
        db.SaveChanges();
    }

    private static async Task AssertCanonicalArtifactsAsync(
        RentalCommandDbContext db,
        DemoLegalTestDependencies legalDocuments)
    {
        db.ChangeTracker.Clear();
        var agreement = await db.LeaseAgreements
            .Include(candidate => candidate.IssuedArtifact)
            .Include(candidate => candidate.ExecutedArtifact)
            .SingleAsync(candidate => candidate.AgreementNumber == "DEMO-AGR-ACTIVE-001-V1");
        agreement.IssuedArtifact.Should().NotBeNull();
        agreement.ExecutedArtifact.Should().NotBeNull();
        agreement.FullyExecutedAtUtc.Should().NotBeNull();

        var artifacts = new[] { agreement.IssuedArtifact!, agreement.ExecutedArtifact! };
        foreach (var artifact in artifacts)
        {
            var bytes = legalDocuments.StoredBytes[artifact.StorageKey];
            bytes.Should().StartWith(Encoding.ASCII.GetBytes("%PDF"));
            artifact.ByteLength.Should().Be(bytes.LongLength);
            artifact.ContentSha256.Should().Be(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        (await db.StoredFiles.CountAsync()).Should().Be(2);
        (await db.LegalDocumentArtifacts.CountAsync()).Should().Be(2);
        (await db.PendingFileUploads.CountAsync()).Should().Be(2);
        (await db.PendingFileUploads.CountAsync(upload =>
            upload.State == PendingFileUploadState.Finalized && upload.StoredFileId != null)).Should().Be(2);
    }
}

internal sealed class InjectedFinalizationException : Exception;

internal sealed class FinalizationFailureInterceptor : SaveChangesInterceptor
{
    public bool Armed { get; set; }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ThrowOnceWhenFinalizing(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ThrowOnceWhenFinalizing(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ThrowOnceWhenFinalizing(DbContext? context)
    {
        if (!Armed || context is null || !context.ChangeTracker.Entries<PendingFileUpload>().Any(entry =>
                entry.State == EntityState.Modified
                && entry.Entity.State == PendingFileUploadState.Finalized))
        {
            return;
        }

        Armed = false;
        throw new InjectedFinalizationException();
    }
}

internal sealed class DemoLegalTestDependencies :
    ILeaseAgreementRenderer,
    ILeaseAgreementPdfGenerator,
    IExecutedLeasePdfGenerator,
    IPendingFileUploadStore,
    IFileStorage
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicExecutionState _atomic;
    private readonly Dictionary<string, byte[]> _stored = new(StringComparer.Ordinal);
    private int _uploadAttempt;

    public int? FailUploadAttempt { get; set; }
    public IReadOnlyDictionary<string, byte[]> StoredBytes => _stored;
    public Dictionary<string, PendingFileUploadAdmission> AdmissionsByPurpose { get; } =
        new(StringComparer.Ordinal);

    public DemoLegalTestDependencies(RentalCommandDbContext db, IAtomicExecutionState atomic)
    {
        _db = db;
        _atomic = atomic;
    }

    public Task<LeaseAgreementRenderResult> RenderAsync(
        int portfolioId, int actorUserId, LeaseAgreementRenderData data, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<LeaseAgreementRenderResult> RenderExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        LeaseAgreementRenderData data,
        Func<byte[]> builtInPdfFactory,
        CancellationToken ct = default)
    {
        EnsureOutsideInfrastructure();
        return Task.FromResult(new LeaseAgreementRenderResult(
            builtInPdfFactory(), documentSourceVersionId, null));
    }

    public byte[] Generate(LeaseAgreementRenderData data)
    {
        EnsureOutsideInfrastructure();
        return Encoding.ASCII.GetBytes("%PDF-issued-demo");
    }

    public byte[] Generate(ExecutedLeaseData data, string contentSha256)
    {
        EnsureOutsideInfrastructure();
        return Encoding.ASCII.GetBytes("%PDF-executed-demo");
    }

    public async Task<PendingFileUploadAdmission> PrepareAsync(
        int portfolioId,
        int actorScopeId,
        string purpose,
        string clientOperationId,
        string requestFingerprint,
        string fileName,
        string contentType,
        long sizeBytes,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        EnsureOutsideInfrastructure();
        var operationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(clientOperationId))).ToLowerInvariant();
        var existing = await _db.PendingFileUploads.SingleOrDefaultAsync(upload =>
            upload.PortfolioId == portfolioId
            && upload.ActorScopeId == actorScopeId
            && upload.Purpose == purpose
            && upload.OperationKeyHash == operationHash, ct);
        if (existing is null)
        {
            existing = new PendingFileUpload
            {
                Id = Guid.NewGuid(),
                PortfolioId = portfolioId,
                ActorScopeId = actorScopeId,
                Purpose = purpose,
                OperationKeyHash = operationHash,
                RequestFingerprint = requestFingerprint,
                StoragePath = $"demo-tests/{portfolioId}/{purpose}/{fileName}",
                FileName = fileName,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                State = PendingFileUploadState.Prepared,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            _db.PendingFileUploads.Add(existing);
            await _db.SaveChangesAsync(ct);
        }
        if (existing.RequestFingerprint != requestFingerprint)
            throw new UploadOperationConflictException(clientOperationId);
        var admission = new PendingFileUploadAdmission(
            existing.Id, existing.StoragePath, existing.State, existing.StoredFileId,
            existing.RequestFingerprint);
        AdmissionsByPurpose[purpose] = admission;
        return admission;
    }

    public Task<string> UploadAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public async Task UploadAtAsync(
        Stream content,
        string storagePath,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        EnsureOutsideInfrastructure();
        _uploadAttempt++;
        if (FailUploadAttempt == _uploadAttempt)
            throw new IOException($"Injected failure for upload attempt {_uploadAttempt}.");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _stored[storagePath] = buffer.ToArray();
    }

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(_stored[path], writable: false));

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        _stored.Remove(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingFileUploadCleanupClaim>> ClaimExpiredAsync(
        string claimOwner, TimeSpan preparedRetention, TimeSpan claimLease, int batchSize,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> MarkAbandonedAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<int> ReleaseCleanupClaimAsync(
        Guid id, string claimOwner, Guid claimToken, CancellationToken ct = default) =>
        throw new NotSupportedException();

    private void EnsureOutsideInfrastructure()
    {
        if (_atomic.IsInfrastructureActive)
            throw new InvalidOperationException("Provider I/O ran inside infrastructure transaction.");
    }
}
