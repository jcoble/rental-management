using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
using RentalCommand.Core.Sandbox;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Sandbox guard + parameterized seeder coverage:
///   * <see cref="SandboxGuard"/> — the Stripe money-moving guard: true only for a sandbox portfolio;
///     false for live/unknown/null.
///   * <see cref="DemoDataSeeder.SeedPortfolioAsync"/> — seeds a fresh arbitrary portfolio id,
///     reconciles only recognized DEMO-LM facts, and never touches the sandbox flag.
///   * Stripe checkout is suppressed (NotEnabled, no Stripe call) while a portfolio is sandbox.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class SandboxGuardAndSeederTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<ServiceProvider> _atomicProviders = [];
    private MigratedPostgreSqlTestContext _ctx = null!;

    public SandboxGuardAndSeederTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        var now = DateTime.UtcNow;
        _ctx = await _fixture.CreateContextAsync();
        _ctx.Db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            UserId = 1,
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
        await _ctx.Db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _atomicProviders)
        {
            await provider.DisposeAsync();
        }
        if (_ctx is not null)
        {
            await _ctx.DisposeAsync();
        }
    }

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
        _ctx.Db.OwnerEntities.Add(new OwnerEntity
        {
            Portfolio = portfolio,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "New Signup",
            IsPrimary = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.WorkspaceAccessContexts.Add(accessContext);
        _ctx.Db.SaveChanges();

        var legalDocuments = new DemoLegalTestDependencies(_ctx.Db);
        var (atomic, atomicContext, _) = BuildAtomicServices(_ctx.Db);
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            atomic,
            atomicContext,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments);
        await seeder.SeedPortfolioAsync(
            2, "seed-arbitrary-portfolio", CancellationToken.None);

        // Demo data landed under portfolio 2, all FK'd correctly.
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.LeaseManagements.CountAsync(l => l.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.TenantLedgerEntries.CountAsync(p => p.PortfolioId == 2)).Should().BeGreaterThan(0);
        (await _ctx.Db.Tenants.IgnoreQueryFilters().CountAsync(t => t.PortfolioId == 2)).Should().BeGreaterThan(0);
        var seededOwners = await _ctx.Db.OwnerEntities.IgnoreQueryFilters()
            .Where(owner => owner.PortfolioId == 2)
            .OrderBy(owner => owner.Id)
            .ToListAsync();
        seededOwners.Should().HaveCount(2);
        seededOwners.Should().ContainSingle(owner =>
            owner.IsPrimary
            && owner.Name == "Maple Ridge Properties LLC"
            && owner.OwnerEntityType == OwnerEntityType.LLC);
        var earliestSeededFinancialActivity = new[]
        {
            await _ctx.Db.TenantLedgerEntries
                .Where(entry => entry.PortfolioId == 2)
                .MinAsync(entry => entry.PostedAtUtc),
            await _ctx.Db.Expenses
                .Where(expense => expense.PortfolioId == 2)
                .MinAsync(expense => expense.PaidAt ?? expense.IncurredAt),
        }.Min();
        var seededOwnerships = await _ctx.Db.PropertyOwnerships
            .Where(ownership => ownership.PortfolioId == 2)
            .ToListAsync();
        seededOwnerships.Should().NotBeEmpty();
        seededOwnerships.Should().OnlyContain(ownership =>
            ownership.EffectiveFromUtc <= earliestSeededFinancialActivity);

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
        var unitAExpense = await _ctx.Db.Expenses.IgnoreQueryFilters()
            .Include(expense => expense.Unit)
            .SingleAsync(expense => expense.PortfolioId == 2
                && expense.Description == "Fix leaking pipe under kitchen sink – Unit A");
        unitAExpense.OperationalScope.Should().Be(ExpenseOperationalScope.Unit);
        unitAExpense.Unit.Should().NotBeNull();
        unitAExpense.Unit!.UnitNumber.Should().Be("A");

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

        var possessionWithoutGoverningAgreement = await (
            from lifecycle in _ctx.Db.LeaseManagementLifecycleProjections
            join relationship in _ctx.Db.LeaseManagements
                on new { lifecycle.PortfolioId, Id = lifecycle.LeaseManagementId }
                equals new { relationship.PortfolioId, relationship.Id }
            where lifecycle.PortfolioId == 2
                && relationship.RelationshipNumber.StartsWith("DEMO-LM-")
                && lifecycle.HasPossessionWithoutGoverningAgreement
            select relationship.RelationshipNumber)
            .ToListAsync();
        possessionWithoutGoverningAgreement.Should().Equal("DEMO-LM-ACTIVE-017");

        var unintendedCurrentAgreementGaps = await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
            agreement.PortfolioId == 2
            && agreement.LeaseManagement!.RelationshipNumber.StartsWith("DEMO-LM-ACTIVE-")
            && agreement.LeaseManagement.RelationshipNumber != "DEMO-LM-ACTIVE-017"
            && agreement.LeaseManagement.PossessionGivenAtUtc != null
            && agreement.LeaseManagement.PossessionReturnedAtUtc == null
            && agreement.BaseRentAmount > 0
            && (agreement.IssuedArtifactId == null
                || agreement.ExecutedArtifactId == null
                || agreement.FullyExecutedAtUtc == null));
        unintendedCurrentAgreementGaps.Should().Be(0,
            "every non-exception current demo relationship with scheduled rent terms must have immutable executed evidence");

        var scheduledRentSourcesWithoutExecutedArtifacts = await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
            agreement.PortfolioId == 2
            && agreement.LeaseManagement!.RelationshipNumber.StartsWith("DEMO-LM-ACTIVE-")
            && agreement.LeaseManagement.RelationshipNumber != "DEMO-LM-ACTIVE-017"
            && agreement.BaseRentAmount > 0
            && agreement.FullyExecutedAtUtc != null
            && agreement.ExecutedArtifactId == null);
        scheduledRentSourcesWithoutExecutedArtifacts.Should().Be(0);

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
    public async Task SeedPortfolio_ExactOperationRetryReplaysOnce()
    {
        var legalDocuments = new DemoLegalTestDependencies(_ctx.Db);
        var (atomic, atomicContext, _) = BuildAtomicServices(_ctx.Db);
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            atomic,
            atomicContext,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments);
        await seeder.SeedPortfolioAsync(
            1, "seed-idempotency", CancellationToken.None);
        var firstCount = await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1);
        firstCount.Should().BeGreaterThan(0);

        // An exact retry reuses the completed receipt and does not duplicate the graph or outbox.
        await seeder.SeedPortfolioAsync(
            1, "seed-idempotency", CancellationToken.None);
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(firstCount);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sandbox.demo-seed"
            && receipt.IdempotencyKey == "portfolio:1:seed-idempotency")).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == "demo-seed/1/seed-idempotency")).Should().Be(1);
    }

    [Fact]
    public async Task SeedPortfolio_PopulatedNonDemoPortfolioIsRejectedWithoutMutation()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Properties.Add(new Property
        {
            PortfolioId = 1,
            Name = "Existing live property",
            AddressLine1 = "1 Existing Way",
            City = "Town",
            State = "ST",
            PostalCode = "00000",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();
        var receiptCount = await _ctx.Db.AtomicCommandReceipts.CountAsync();
        var outboxCount = await _ctx.Db.OutboxMessages.CountAsync();
        var (seeder, _) = BuildSeeder(_ctx.Db);

        var action = () => seeder.SeedPortfolioAsync(
            1, "reject-populated-nondemo", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*populated non-demo portfolio*");
        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.Properties.CountAsync(property => property.PortfolioId == 1)).Should().Be(1);
        (await _ctx.Db.LeaseManagements.CountAsync(relationship =>
            relationship.PortfolioId == 1
            && relationship.RelationshipNumber.StartsWith("DEMO-LM-"))).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(receiptCount);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(outboxCount);
    }

    [Fact]
    public async Task SeedPortfolio_FirstPassUsesContentAddressedLegalUploadRegistrations()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);

        var action = () => seeder.SeedPortfolioAsync(
            1, "seed-first-pass-content-addressed", CancellationToken.None);

        await action.Should().NotThrowAsync<UploadOperationConflictException>();
        legalDocuments.OperationIdsByPurpose.Should().ContainKey("demo-legal-issued");
        legalDocuments.OperationIdsByPurpose["demo-legal-issued"]
            .Should().EndWith(legalDocuments.AdmissionsByPurpose["demo-legal-issued"].RequestFingerprint);
        legalDocuments.OperationIdsByPurpose.Should().ContainKey("demo-legal-executed");
        legalDocuments.OperationIdsByPurpose["demo-legal-executed"]
            .Should().EndWith(legalDocuments.AdmissionsByPurpose["demo-legal-executed"].RequestFingerprint);
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_FreshPostgreSqlDatabaseSucceedsOnFirstPassWithRealPendingUploadStore()
    {
        var legalDocuments = new DemoLegalTestDependencies(_ctx.Db);
        var (atomic, atomicContext, pendingUploads) = BuildAtomicServices(_ctx.Db);
        var seeder = new DemoDataSeeder(
            _ctx.Db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            atomic,
            atomicContext,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            pendingUploads,
            legalDocuments);

        var action = () => seeder.SeedPortfolioAsync(
            1, "seed-first-pass-real-pending-store", CancellationToken.None);

        await action.Should().NotThrowAsync<UploadOperationConflictException>();
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_FailureBeforeFirstUpload_RetryCompletesWithoutPlaceholderOrDuplicates()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        legalDocuments.FailUploadAttempt = 1;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(
            1, "seed-first-upload-retry", CancellationToken.None);
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
        await seeder.SeedPortfolioAsync(
            1, "seed-first-upload-retry", CancellationToken.None);

        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_SecondUploadFailure_ReusesAdmissionsAndPathsWithoutDuplicates()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        legalDocuments.FailUploadAttempt = 2;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(
            1, "seed-second-upload-retry", CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<IOException>();

        var firstAdmissions = legalDocuments.AdmissionsByOperationId
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        firstAdmissions.Should().HaveCount(2);
        var firstIssuedAdmission = legalDocuments.AdmissionsByPurpose["demo-legal-issued"];
        var firstExecutedAdmission = legalDocuments.AdmissionsByPurpose["demo-legal-executed"];
        (await _ctx.Db.PendingFileUploads.CountAsync()).Should().Be(2);
        (await _ctx.Db.LegalDocumentArtifacts.CountAsync()).Should().Be(0);
        legalDocuments.StoredBytes.Should().ContainKey(firstIssuedAdmission.StoragePath);
        legalDocuments.StoredBytes.Should().NotContainKey(firstExecutedAdmission.StoragePath);

        legalDocuments.FailUploadAttempt = null;
        await seeder.SeedPortfolioAsync(
            1, "seed-second-upload-retry", CancellationToken.None);

        foreach (var (operationId, firstAdmission) in firstAdmissions)
        {
            legalDocuments.AdmissionsByOperationId.Should().ContainKey(operationId)
                .WhoseValue.Should().BeEquivalentTo(firstAdmission);
        }
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_FinalizationFailure_RollsBackAllMetadataAndRetryCompletesOnce()
    {
        var failure = new FinalizationFailureInterceptor();
        await using var context = await _fixture.CreateContextAsync([failure]);
        SeedAdministeringAccess(context.Db);
        var (seeder, legalDocuments) = BuildSeeder(context.Db, [failure]);
        failure.Armed = true;

        Func<Task> firstAttempt = () => seeder.SeedPortfolioAsync(
            1, "seed-finalization-retry", CancellationToken.None);
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

        await seeder.SeedPortfolioAsync(
            1, "seed-finalization-retry", CancellationToken.None);

        await AssertCanonicalArtifactsAsync(context.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_WithExistingProperties_ReconcilesCurrentFactsAndArtifacts()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        await seeder.SeedPortfolioAsync(
            1, "seed-before-reconciliation", CancellationToken.None);
        var relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.RelationshipNumber == "DEMO-LM-ACTIVE-017");
        var plannedMoveOutAtUtc = DateTime.UtcNow.Date.AddMonths(1).AddHours(16);
        relationship.PlannedMoveOutAtUtc = plannedMoveOutAtUtc;
        relationship.PossessionAgreementExceptionReason = null;
        relationship.PossessionAgreementExceptionAuthorizedByUserId = null;
        await _ctx.Db.SaveChangesAsync();

        await seeder.SeedPortfolioAsync(
            1, "reconcile-existing-demo-facts", CancellationToken.None);

        _ctx.Db.ChangeTracker.Clear();
        relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.RelationshipNumber == "DEMO-LM-ACTIVE-017");
        relationship.PlannedMoveOutAtUtc.Should().Be(plannedMoveOutAtUtc);
        relationship.EndingDispositionDecidedAtUtc.Should().BeNull();
        relationship.EndingDispositionDecidedByUserId.Should().BeNull();
        relationship.PossessionAgreementExceptionReason.Should().NotBeNullOrWhiteSpace();
        relationship.PossessionAgreementExceptionAuthorizedByUserId.Should().NotBeNull();
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sandbox.demo-seed"
            && (receipt.IdempotencyKey == "portfolio:1:seed-before-reconciliation"
                || receipt.IdempotencyKey == "portfolio:1:reconcile-existing-demo-facts"))).Should().Be(2);
        (await _ctx.Db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == "demo-seed/1/seed-before-reconciliation"
            || message.IdempotencyKey == "demo-seed/1/reconcile-existing-demo-facts")).Should().Be(2);
        await AssertCanonicalArtifactsAsync(_ctx.Db, legalDocuments);
    }

    [Fact]
    public async Task SeedPortfolio_ReconcilePreservesPendingMoveOutAndUnchangedRowVersion()
    {
        var (seeder, _) = BuildSeeder(_ctx.Db);
        await seeder.SeedPortfolioAsync(
            1, "seed-before-live-move-out", CancellationToken.None);

        var relationship = await _ctx.Db.LeaseManagements
            .Where(candidate =>
                candidate.RelationshipNumber.StartsWith("DEMO-LM-")
                && candidate.RelationshipNumber != "DEMO-LM-ACTIVE-017"
                && candidate.PossessionGivenAtUtc != null
                && candidate.PossessionReturnedAtUtc == null
                && candidate.EndingDisposition == LeaseManagementEndingDisposition.Undecided
                && candidate.TenantAccount != null
                && candidate.TenantAccount.ClosedAtUtc == null)
            .OrderBy(candidate => candidate.RelationshipNumber)
            .FirstAsync();
        relationship.PlannedMoveOutAtUtc = DateTime.UtcNow.Date.AddMonths(1).AddHours(17);
        await _ctx.Db.SaveChangesAsync();

        _ctx.Db.ChangeTracker.Clear();
        relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.Id == relationship.Id);
        var plannedMoveOutAtUtc = relationship.PlannedMoveOutAtUtc;
        var rowVersion = relationship.RowVersion;

        await seeder.SeedPortfolioAsync(
            1, "reconcile-after-live-move-out", CancellationToken.None);

        _ctx.Db.ChangeTracker.Clear();
        relationship = await _ctx.Db.LeaseManagements.SingleAsync(candidate =>
            candidate.Id == relationship.Id);
        relationship.PlannedMoveOutAtUtc.Should().Be(plannedMoveOutAtUtc);
        relationship.RowVersion.Should().Be(rowVersion);
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

    private (DemoDataSeeder Seeder, DemoLegalTestDependencies LegalDocuments) BuildSeeder(
        RentalCommandDbContext db,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var legalDocuments = new DemoLegalTestDependencies(db);
        var (atomic, atomicContext, _) = BuildAtomicServices(db, interceptors);
        return (new DemoDataSeeder(
            db,
            NullLogger<DemoDataSeeder>.Instance,
            TimeProvider.System,
            atomic,
            atomicContext,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments,
            legalDocuments), legalDocuments);
    }

    private (IAtomicUnitOfWork Atomic, IAtomicCommandContext Context, IPendingFileUploadStore PendingUploads)
        BuildAtomicServices(
        RentalCommandDbContext db,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, DemoSeedTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            SeedDemoPortfolioCommand,
            SeedDemoPortfolioResult,
            DemoSeedCommandHandler>();
        services.AddAtomicCommandHandler<
            FinalizeDemoLegalDocumentCommand,
            FinalizeDemoLegalDocumentResult,
            DemoLegalDocumentFinalizeCommandHandler>();
        services.AddPendingFileUploadStore();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(db.Database.GetConnectionString());
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
            builder.UseAtomicPersistenceKernel(provider);
        });
        var serviceProvider = services.BuildServiceProvider();
        _atomicProviders.Add(serviceProvider);
        return (
            serviceProvider.GetRequiredService<IAtomicUnitOfWork>(),
            serviceProvider.GetRequiredService<IAtomicCommandContext>(),
            serviceProvider.GetRequiredService<IPendingFileUploadStore>());
    }

    private sealed class DemoSeedTestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "test:demo-seed";
        public string? IpAddress => "127.0.0.1";
    }

    private static void SeedAdministeringAccess(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            UserId = 1,
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
        var agreements = await db.LeaseAgreements
            .Include(candidate => candidate.IssuedArtifact)
            .Include(candidate => candidate.ExecutedArtifact)
            .Where(candidate => candidate.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && candidate.LeaseManagement!.RelationshipNumber != "DEMO-LM-ACTIVE-017")
            .OrderBy(candidate => candidate.AgreementNumber)
            .ToListAsync();
        agreements.Should().NotBeEmpty();

        foreach (var agreement in agreements)
        {
            agreement.IssuedArtifact.Should().NotBeNull();
            agreement.ExecutedArtifact.Should().NotBeNull();
            agreement.FullyExecutedAtUtc.Should().NotBeNull();

            foreach (var artifact in new[] { agreement.IssuedArtifact!, agreement.ExecutedArtifact! })
            {
                var bytes = legalDocuments.StoredBytes[artifact.StorageKey];
                bytes.Should().StartWith(Encoding.ASCII.GetBytes("%PDF"));
                artifact.ByteLength.Should().Be(bytes.LongLength);
                artifact.ContentSha256.Should().Be(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
        }

        var expectedArtifactCount = agreements.Count * 2;
        (await db.StoredFiles.CountAsync()).Should().Be(expectedArtifactCount);
        (await db.LegalDocumentArtifacts.CountAsync()).Should().Be(expectedArtifactCount);
        (await db.PendingFileUploads.CountAsync()).Should().Be(expectedArtifactCount);
        (await db.PendingFileUploads.CountAsync(upload =>
            upload.State == PendingFileUploadState.Finalized && upload.StoredFileId != null))
            .Should().Be(expectedArtifactCount);
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
    private readonly Dictionary<string, byte[]> _stored = new(StringComparer.Ordinal);
    private int _uploadAttempt;

    public int? FailUploadAttempt { get; set; }
    public IReadOnlyDictionary<string, byte[]> StoredBytes => _stored;
    public Dictionary<string, PendingFileUploadAdmission> AdmissionsByPurpose { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, PendingFileUploadAdmission> AdmissionsByOperationId { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> OperationIdsByPurpose { get; } =
        new(StringComparer.Ordinal);

    public DemoLegalTestDependencies(RentalCommandDbContext db) => _db = db;

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
        OperationIdsByPurpose[purpose] = clientOperationId;
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
        AdmissionsByOperationId[clientOperationId] = admission;
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
        if (_db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Provider I/O ran inside infrastructure transaction.");
    }
}
