using System.Data.Common;
using System.Globalization;
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
using RentalCommand.Core.Leasing;
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
[Collection(MigratedPostgreSqlCollection.Name2)]
public class SandboxGuardAndSeederTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<ServiceProvider> _atomicProviders = [];
    private readonly DemoPortfolioDiscoveryCommandInterceptor _demoPortfolioDiscovery = new();
    private MigratedPostgreSqlTestContext _ctx = null!;

    public SandboxGuardAndSeederTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        var now = DateTime.UtcNow;
        _ctx = await _fixture.CreateContextAsync([_demoPortfolioDiscovery]);
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

        var (atomic, atomicContext, _, atomicDb) = BuildAtomicServices(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        var seeder = new DemoDataSeeder(
            atomicDb,
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
        _ctx.Db.ChangeTracker.Clear();

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

        var seededAddendumTemplate = await _ctx.Db.DocumentTemplates
            .SingleAsync(template => template.PortfolioId == 2
                && template.Name == "Standard lease addendum page");
        seededAddendumTemplate.Kind.Should().Be(DocumentTemplateKind.Lease);
        seededAddendumTemplate.Status.Should().Be(DocumentTemplateStatus.Active);
        seededAddendumTemplate.ArchivedAtUtc.Should().BeNull();
        seededAddendumTemplate.PropertyId.Should().BeNull();
        seededAddendumTemplate.IsSandboxSeeded.Should().BeTrue();
        seededAddendumTemplate.OriginalStoredFileId.Should().Be(executedDemo.IssuedArtifact.StoredFileId);

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
    public async Task DemoDataSeeder_ExistingDemoWithoutOwnerships_RepairsOwnerReportData()
    {
        var (seeder, _) = BuildSeeder(_ctx.Db);
        await seeder.SeedPortfolioAsync(1, "seed-owner-report-initial", CancellationToken.None);
        await _ctx.Db.PropertyOwnerships
            .Where(ownership => ownership.PortfolioId == 1)
            .ExecuteDeleteAsync();

        await seeder.SeedPortfolioAsync(1, "seed-owner-report-repair", CancellationToken.None);
        await seeder.SeedPortfolioAsync(1, "seed-owner-report-idempotency", CancellationToken.None);

        var ownerIds = await _ctx.Db.PropertyOwnerships
            .Where(ownership => ownership.PortfolioId == 1)
            .Select(ownership => ownership.OwnerEntityId)
            .Distinct()
            .ToListAsync();
        ownerIds.Should().HaveCount(2);
        (await _ctx.Db.Properties.CountAsync(property => property.PortfolioId == 1))
            .Should().Be(await _ctx.Db.PropertyOwnerships.CountAsync(ownership => ownership.PortfolioId == 1));
    }

    [Fact]
    public async Task SeedPortfolio_ExactOperationRetryReplaysOnce()
    {
        var (atomic, atomicContext, _, atomicDb) = BuildAtomicServices(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        var seeder = new DemoDataSeeder(
            atomicDb,
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
        (await _ctx.Db.DocumentTemplates.CountAsync(template =>
            template.PortfolioId == 1
            && template.Name == "Standard lease addendum page")).Should().Be(1);

        // An exact retry reuses the completed receipt and does not duplicate the graph or outbox.
        await seeder.SeedPortfolioAsync(
            1, "seed-idempotency", CancellationToken.None);
        (await _ctx.Db.Properties.IgnoreQueryFilters().CountAsync(p => p.PortfolioId == 1)).Should().Be(firstCount);
        (await _ctx.Db.DocumentTemplates.CountAsync(template =>
            template.PortfolioId == 1
            && template.Name == "Standard lease addendum page")).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sandbox.demo-seed"
            && receipt.IdempotencyKey == "portfolio:1:seed-idempotency")).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == "demo-seed/1/seed-idempotency")).Should().Be(1);
    }

    [Fact]
    public async Task SeedPortfolio_FrozenLegacyReceiptReplaysThroughProductionCallerAndReauthorizes()
    {
        const string operationKey = "frozen-demo-seed";
        const string key = "portfolio:1:frozen-demo-seed";
        // Frozen base-caller fingerprint calculated once from the legacy command; never regenerate.
        const string fingerprint = "b2e92f67a91c61c5e4859e8dede78600dbc6a3e0a6ce6d5877d03259573664c9";
        const string resultJson = """
            {"PortfolioId":1,"AlreadyPresent":false,"OwnerEntityCount":0,"VendorCount":0,"PropertyCount":0,"UnitCount":0,"TenantCount":0,"ActiveLeaseManagementCount":0,"EndedLeaseManagementCount":0,"TenantLedgerEntryCount":0,"SecurityDepositAccountCount":0,"ExpenseCount":0,"WorkOrderCount":0,"AppointmentCount":0,"InspectionCount":0,"LegalAgreementId":null}
            """;
        AtomicCommandFingerprint.Create(new SeedDemoPortfolioCommand(
                1, false, DateTime.UnixEpoch, operationKey))
            .Should().Be(fingerprint);

        var (writes, atomicContext, _, atomicDb) = BuildAtomicServices(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        var seeder = new DemoDataSeeder(
            atomicDb, NullLogger<DemoDataSeeder>.Instance, TimeProvider.System,
            writes, atomicContext, legalDocuments, legalDocuments, legalDocuments,
            legalDocuments, legalDocuments);
        await seeder.SeedPortfolioAsync(
            1, "prepare-frozen-demo-seed", CancellationToken.None);
        var propertyCount = await atomicDb.Properties.IgnoreQueryFilters()
            .CountAsync(property => property.PortfolioId == 1);

        atomicDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = "sandbox.demo-seed",
            IdempotencyKey = key,
            RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = "demo-portfolio-seed-result:v1",
            ResultJson = resultJson,
            StartedAt = DateTime.UnixEpoch,
            CompletedAt = DateTime.UnixEpoch,
        });
        await atomicDb.SaveChangesAsync();
        atomicDb.ChangeTracker.Clear();

        await seeder.SeedPortfolioAsync(1, operationKey, CancellationToken.None);

        (await atomicDb.Properties.IgnoreQueryFilters().CountAsync(property => property.PortfolioId == 1))
            .Should().Be(propertyCount, "a legacy receipt replay must not seed the demo graph again");
        (await atomicDb.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sandbox.demo-seed" && receipt.IdempotencyKey == key))
            .Should().Be(1);

        const string forbiddenKey = "portfolio:999:frozen-demo-seed";
        const string forbiddenResultJson = """
            {"PortfolioId":999,"AlreadyPresent":false,"OwnerEntityCount":0,"VendorCount":0,"PropertyCount":0,"UnitCount":0,"TenantCount":0,"ActiveLeaseManagementCount":0,"EndedLeaseManagementCount":0,"TenantLedgerEntryCount":0,"SecurityDepositAccountCount":0,"ExpenseCount":0,"WorkOrderCount":0,"AppointmentCount":0,"InspectionCount":0,"LegalAgreementId":null}
            """;
        atomicDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = "sandbox.demo-seed",
            IdempotencyKey = forbiddenKey,
            RequestFingerprint = "b80fc9e9852df2221357abd3b2fdf85a735dbd333c967a0f6a5dbafab55d5a06",
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = "demo-portfolio-seed-result:v1",
            ResultJson = forbiddenResultJson,
            StartedAt = DateTime.UnixEpoch,
            CompletedAt = DateTime.UnixEpoch,
        });
        await atomicDb.SaveChangesAsync();
        atomicDb.ChangeTracker.Clear();

        var forbidden = () => seeder.SeedPortfolioAsync(999, operationKey, CancellationToken.None);
        await forbidden.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CompleteLegalArtifacts_FrozenLegacyReceiptsReplayWithoutReexecutionAndReauthorize()
    {
        var businessNowUtc = new DateTime(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var (writes, atomicContext, _, atomicDb) = BuildAtomicServices(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        legalDocuments.AdmissionIdsByPurpose["demo-legal-issued"] =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        legalDocuments.AdmissionIdsByPurpose["demo-legal-executed"] =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var seeder = new DemoDataSeeder(
            atomicDb, NullLogger<DemoDataSeeder>.Instance,
            new FixedDemoTimeProvider(businessNowUtc), writes, atomicContext,
            legalDocuments, legalDocuments, legalDocuments, legalDocuments, legalDocuments);

        var setupCommand = new SeedDemoPortfolioCommand(
            1, false, businessNowUtc, "frozen-demo-legal-setup");
        await writes.ExecuteAsync(
            "portfolio:1:frozen-demo-legal-setup",
            DemoSeedCommandRule.Write(atomicDb, setupCommand), CancellationToken.None);
        var candidateAgreements = await atomicDb.LeaseAgreements
            .Where(agreement => agreement.PortfolioId == 1
                && agreement.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-"))
            .OrderBy(agreement => agreement.AgreementNumber)
            .ToListAsync();
        candidateAgreements.Should().HaveCountGreaterThan(1);
        foreach (var agreement in candidateAgreements.Skip(1))
        {
            agreement.AgreementNumber = $"IGNORED-{agreement.AgreementNumber}";
        }
        await atomicDb.SaveChangesAsync();
        atomicDb.ChangeTracker.Clear();

        var intent = (await CanonicalDemoLeaseSeeder.BuildLegalDocumentIntentsAsync(
            atomicDb, 1, 1, CancellationToken.None)).Should().ContainSingle().Subject;
        intent.AgreementId.Should().Be(1);
        var issuedBytes = Encoding.ASCII.GetBytes("%PDF-issued-demo");
        var executedBytes = Encoding.ASCII.GetBytes("%PDF-executed-demo");
        var issuedHash = Convert.ToHexString(SHA256.HashData(issuedBytes)).ToLowerInvariant();
        var executedHash = Convert.ToHexString(SHA256.HashData(executedBytes)).ToLowerInvariant();
        var issuedFileName = $"{intent.RenderData.AgreementNumber}-issued.pdf";
        var executedFileName = $"{intent.RenderData.AgreementNumber}-executed.pdf";
        var issuanceFingerprint = LegalDocumentIssuanceBinding.Create(
            nameof(LeaseAgreement), 1, intent.LeaseManagementId, intent.AgreementId,
            intent.DraftRevision, intent.DocumentSourceVersionId, intent.TermsSchemaVersion,
            intent.TermsPayload, issuedHash, issuedBytes.LongLength, issuedFileName);
        var legalCommand = new FinalizeDemoLegalDocumentCommand(
            1, 1, intent.AgreementId,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            $"demo-tests/1/demo-legal-issued/{issuedFileName}", issuanceFingerprint,
            issuedFileName, issuedBytes.LongLength, issuedHash, issuanceFingerprint,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            $"demo-tests/1/demo-legal-executed/{executedFileName}", executedHash,
            executedFileName, executedBytes.LongLength, executedHash,
            intent.IssuedAtUtc, intent.ExecutedAtUtc);
        // Frozen base-caller fingerprints calculated once from these legacy commands; never regenerate.
        const string legalFingerprint = "9bcf822e569a524e908d7d0a76566acbc197db0a833c38158d4356d3277bf2bc";
        const string addendumFingerprint = "c9e1334bcb93cc847e3db4dc6437c3d4b66ef78607649f8e2782502ce8e86157";
        AtomicCommandFingerprint.Create(legalCommand).Should().Be(legalFingerprint);
        AtomicCommandFingerprint.Create(new EnsureDemoLeaseAddendumTemplateCommand(
            1, 1, businessNowUtc)).Should().Be(addendumFingerprint);

        atomicDb.AtomicCommandReceipts.AddRange(
            FrozenReceipt("sandbox.demo-legal-finalize", "portfolio:1:agreement:1:v1",
                legalFingerprint, "demo-legal-document-finalize-result:v2",
                "{\"PortfolioId\":1,\"AgreementId\":1,\"IssuedArtifactId\":9901,\"ExecutedArtifactId\":9902,\"AlreadyFinalized\":false,\"Skipped\":false}"),
            FrozenReceipt("sandbox.demo-addendum-template",
                "portfolio:1:standard-lease-addendum-template:v2", addendumFingerprint,
                "demo-lease-addendum-template-result:v1",
                "{\"PortfolioId\":1,\"DocumentTemplateId\":9903,\"AlreadyPresent\":false,\"ReusedIssuedLeasePdf\":true}"));
        await atomicDb.SaveChangesAsync();
        atomicDb.ChangeTracker.Clear();
        var artifactRows = await atomicDb.LegalDocumentArtifacts.CountAsync();
        var templateRows = await atomicDb.DocumentTemplates.CountAsync();
        var outboxRows = await atomicDb.OutboxMessages.CountAsync();

        await seeder.CompleteLegalArtifactsAsync(1, CancellationToken.None);

        (await atomicDb.LegalDocumentArtifacts.CountAsync()).Should().Be(artifactRows);
        (await atomicDb.DocumentTemplates.CountAsync()).Should().Be(templateRows);
        (await atomicDb.OutboxMessages.CountAsync()).Should().Be(outboxRows);
        (await atomicDb.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "sandbox.demo-legal-finalize"
            || receipt.CommandType == "sandbox.demo-addendum-template")).Should().Be(2);

        var access = await atomicDb.WorkspaceAccessContexts
            .SingleAsync(context => context.PortfolioId == 1 && context.UserId == 1);
        access.Status = WorkspaceAccessContextStatus.Revoked;
        access.RevokedAtUtc = businessNowUtc;
        await atomicDb.SaveChangesAsync();
        var forbidden = () => seeder.CompleteLegalArtifactsAsync(1, CancellationToken.None);
        await forbidden.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SeedPortfolio_ArchivedSameNameTemplateGetsUsableCompanionWithoutDuplicates()
    {
        var archivedAtUtc = DateTime.UtcNow.AddDays(-1);
        var archivedTemplate = new DocumentTemplate
        {
            PortfolioId = 1,
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Status = DocumentTemplateStatus.Archived,
            Name = "Standard lease addendum page",
            IsSandboxSeeded = false,
            PropertyId = null,
            Version = 1,
            CreatedAtUtc = archivedAtUtc.AddDays(-1),
            UpdatedAtUtc = archivedAtUtc,
            ArchivedAtUtc = archivedAtUtc,
        };
        _ctx.Db.DocumentTemplates.Add(archivedTemplate);
        await _ctx.Db.SaveChangesAsync();

        var (seeder, _) = BuildSeeder(_ctx.Db);
        await seeder.SeedPortfolioAsync(1, "seed-archived-template", CancellationToken.None);
        await seeder.SeedPortfolioAsync(1, "seed-archived-template", CancellationToken.None);

        _ctx.Db.ChangeTracker.Clear();
        var templates = await _ctx.Db.DocumentTemplates.IgnoreQueryFilters()
            .Where(template => template.PortfolioId == 1
                && template.Name == "Standard lease addendum page")
            .OrderBy(template => template.Id)
            .ToListAsync();

        templates.Should().HaveCount(2);
        templates.Should().ContainSingle(template =>
            template.Status == DocumentTemplateStatus.Active
            && template.ArchivedAtUtc == null
            && template.Kind == DocumentTemplateKind.Lease
            && template.PropertyId == null);
        templates.Should().ContainSingle(template =>
            template.Id == archivedTemplate.Id
            && template.Status == DocumentTemplateStatus.Archived
            && template.ArchivedAtUtc == archivedAtUtc);
    }

    [Fact]
    public async Task DemoDataSeeder_UpgradesCompletedV1TemplateReceiptWithArchivedTemplateWithoutDuplicates()
    {
        var (seeder, _) = BuildSeeder(_ctx.Db);
        const string operationKey = "seed-template-reconciliation-upgrade";

        // Establish the pre-upgrade state: simulate the old v1 reconciliation having completed,
        // then archive its compatible template so only an incompatible same-name row remains.
        await seeder.SeedPortfolioAsync(1, operationKey, CancellationToken.None);
        var archivedAtUtc = DateTime.UtcNow.AddDays(-1);
        var seededTemplate = await _ctx.Db.DocumentTemplates
            .SingleAsync(template => template.PortfolioId == 1
                && template.Name == "Standard lease addendum page"
                && template.Status == DocumentTemplateStatus.Active
                && template.ArchivedAtUtc == null
                && template.Kind == DocumentTemplateKind.Lease
                && template.PropertyId == null);
        seededTemplate.Status = DocumentTemplateStatus.Archived;
        seededTemplate.ArchivedAtUtc = archivedAtUtc;
        seededTemplate.UpdatedAtUtc = archivedAtUtc;
        var templateReceipt = await _ctx.Db.AtomicCommandReceipts
            .SingleAsync(receipt =>
                receipt.CommandType == DemoLeaseAddendumTemplateCommandRule.CommandType
                && receipt.IdempotencyKey == "portfolio:1:standard-lease-addendum-template:v2"
                && receipt.Status == AtomicCommandReceiptStatus.Completed);
        templateReceipt.IdempotencyKey = "portfolio:1:standard-lease-addendum-template:v1";
        var templateAuditLogs = await _ctx.Db.AtomicAuditLogs
            .Where(audit => audit.CommandType == DemoLeaseAddendumTemplateCommandRule.CommandType
                && audit.CommandIdempotencyKey == "portfolio:1:standard-lease-addendum-template:v2")
            .ToListAsync();
        foreach (var audit in templateAuditLogs)
        {
            audit.CommandIdempotencyKey = "portfolio:1:standard-lease-addendum-template:v1";
        }
        await _ctx.Db.SaveChangesAsync();

        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == DemoLeaseAddendumTemplateCommandRule.CommandType
            && receipt.IdempotencyKey == "portfolio:1:standard-lease-addendum-template:v1"
            && receipt.Status == AtomicCommandReceiptStatus.Completed)).Should().Be(1);

        await seeder.SeedPortfolioAsync(1, operationKey, CancellationToken.None);
        await seeder.SeedPortfolioAsync(1, operationKey, CancellationToken.None);

        _ctx.Db.ChangeTracker.Clear();
        var templates = await _ctx.Db.DocumentTemplates.IgnoreQueryFilters()
            .Where(template => template.PortfolioId == 1
                && template.Name == "Standard lease addendum page")
            .OrderBy(template => template.Id)
            .ToListAsync();

        templates.Should().HaveCount(2);
        templates.Should().ContainSingle(template =>
            template.Status == DocumentTemplateStatus.Active
            && template.ArchivedAtUtc == null
            && template.Kind == DocumentTemplateKind.Lease
            && template.PropertyId == null);
        templates.Should().ContainSingle(template =>
            template.Id == seededTemplate.Id
            && template.Status == DocumentTemplateStatus.Archived
            && template.ArchivedAtUtc == archivedAtUtc);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == DemoLeaseAddendumTemplateCommandRule.CommandType
            && receipt.IdempotencyKey == "portfolio:1:standard-lease-addendum-template:v2"
            && receipt.Status == AtomicCommandReceiptStatus.Completed)).Should().Be(1);
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
    public async Task SeedAsync_ReconcilesEveryDemoPortfolio_AndSkipsPopulatedNonDemoPortfolio()
    {
        var now = DateTime.UtcNow;
        var demoPortfolio = new Portfolio
        {
            Id = 2,
            Name = "Second demo portfolio",
            ManagementCompanyName = "Second Demo Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var nonDemoPortfolio = new Portfolio
        {
            Id = 3,
            Name = "Existing live portfolio",
            ManagementCompanyName = "Existing Live Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Portfolios.AddRange(demoPortfolio, nonDemoPortfolio);
        _ctx.Db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            UserId = 1,
            Portfolio = demoPortfolio,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Membership = new WorkspaceMembership
            {
                PortfolioId = 2,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Management,
                EffectiveFromUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            },
        });
        _ctx.Db.Properties.Add(new Property
        {
            Portfolio = nonDemoPortfolio,
            Name = "Existing live property",
            AddressLine1 = "3 Existing Way",
            City = "Town",
            State = "ST",
            PostalCode = "00003",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _ctx.Db.SaveChangesAsync();

        var (firstPreparationSeeder, firstLegalDocuments) = BuildSeeder(_ctx.Db);
        firstLegalDocuments.FailUploadAttempt = 1;
        await FluentActions.Awaiting(() => firstPreparationSeeder.SeedPortfolioAsync(
                1, "prepare-startup-demo-one", CancellationToken.None))
            .Should().ThrowAsync<IOException>();

        var (secondPreparationSeeder, secondLegalDocuments) = BuildSeeder(_ctx.Db);
        secondLegalDocuments.FailUploadAttempt = 1;
        await FluentActions.Awaiting(() => secondPreparationSeeder.SeedPortfolioAsync(
                2, "prepare-startup-demo-two", CancellationToken.None))
            .Should().ThrowAsync<IOException>();

        _ctx.Db.ChangeTracker.Clear();
        foreach (var portfolioId in new[] { 1, 2 })
        {
            (await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
                agreement.PortfolioId == portfolioId
                && agreement.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && agreement.ExecutedArtifactId == null))
                .Should().BeGreaterThan(0);
        }
        var nonDemoPropertyCount = await _ctx.Db.Properties.CountAsync(property =>
            property.PortfolioId == 3);
        var nonDemoReceiptCount = await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.IdempotencyKey.StartsWith("portfolio:3:"));

        var (startupSeeder, _) = BuildSeeder(_ctx.Db, [_demoPortfolioDiscovery]);
        await FluentActions.Awaiting(() => startupSeeder.SeedAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        _demoPortfolioDiscovery.UsedFunction.Should().BeTrue(
            "Npgsql startup discovery must use the RLS-safe database function");

        _ctx.Db.ChangeTracker.Clear();
        foreach (var portfolioId in new[] { 1, 2 })
        {
            (await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
                agreement.PortfolioId == portfolioId
                && agreement.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && agreement.LeaseManagement!.RelationshipNumber != "DEMO-LM-ACTIVE-017"
                && (agreement.ExecutedArtifactId == null
                    || agreement.FullyExecutedAtUtc == null)))
                .Should().Be(0);
            (await _ctx.Db.LeaseAgreements.CountAsync(agreement =>
                agreement.PortfolioId == portfolioId
                && agreement.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && agreement.ExecutedArtifactId != null
                && agreement.FullyExecutedAtUtc != null))
                .Should().BeGreaterThan(0);
        }
        (await _ctx.Db.Properties.CountAsync(property => property.PortfolioId == 3))
            .Should().Be(nonDemoPropertyCount);
        (await _ctx.Db.LeaseManagements.CountAsync(relationship => relationship.PortfolioId == 3))
            .Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.IdempotencyKey.StartsWith("portfolio:3:")))
            .Should().Be(nonDemoReceiptCount);
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
        var (atomic, atomicContext, pendingUploads, atomicDb) = BuildAtomicServices(_ctx.Db);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        var seeder = new DemoDataSeeder(
            atomicDb,
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

        var rent = await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == 1
                && entry.EntryType == TenantLedgerEntryType.RentCharge
                && entry.BusinessKey.Contains(":rent:"))
            .OrderBy(entry => entry.Id)
            .FirstAsync();
        var rentPeriod = rent.BusinessKey.Split(':').Last();
        var expectedRentPeriod = DateTime.ParseExact(
            rentPeriod, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        rent.Description.Should().Be($"Rent for {expectedRentPeriod}");
        rent.BusinessKey.Should().MatchRegex(@":rent:\d{4}-\d{2}$");

        var payment = await _ctx.Db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == 1
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                && entry.BusinessKey.Contains(":rent-payment:"))
            .OrderBy(entry => entry.Id)
            .FirstAsync();
        var paymentPeriod = payment.BusinessKey.Split(':').Last();
        var expectedPaymentPeriod = DateTime.ParseExact(
            paymentPeriod, "yyyy-MM", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        payment.Description.Should().Be($"Rent payment for {expectedPaymentPeriod}");
        payment.BusinessKey.Should().MatchRegex(@":rent-payment:\d{4}-\d{2}$");
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
    public async Task BuildLegalDocumentIntents_PartiallyBoundAgreementIsExcluded()
    {
        var (seeder, legalDocuments) = BuildSeeder(_ctx.Db);
        legalDocuments.FailUploadAttempt = 1;
        await FluentActions.Awaiting(() => seeder.SeedPortfolioAsync(
                1, "prepare-partially-bound-candidate", CancellationToken.None))
            .Should().ThrowAsync<IOException>();

        var agreement = await _ctx.Db.LeaseAgreements
            .Where(candidate => candidate.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && candidate.LeaseManagement!.RelationshipNumber != "DEMO-LM-ACTIVE-017")
            .OrderBy(candidate => candidate.AgreementNumber)
            .FirstAsync();
        var issuedArtifact = await BindRealIssuedArtifactAsync(_ctx.Db, agreement);

        _ctx.Db.ChangeTracker.Clear();
        var actorUserId = await _ctx.Db.Users.OrderBy(user => user.Id).Select(user => user.Id).FirstAsync();
        var intents = await CanonicalDemoLeaseSeeder.BuildLegalDocumentIntentsAsync(
            _ctx.Db, 1, actorUserId, CancellationToken.None);

        intents.Should().NotContain(intent => intent.AgreementId == agreement.Id);
        intents.Should().Contain(intent => intent.AgreementId != agreement.Id,
            "fully unbound demo drafts still need deterministic legal-document backfill");
        var preservedAgreement = await _ctx.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == agreement.Id);
        preservedAgreement.IssuedArtifactId.Should().Be(issuedArtifact.Id);
        preservedAgreement.ExecutedArtifactId.Should().BeNull();
        preservedAgreement.FullyExecutedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task SeedPortfolio_ReconcileFinalizesUnboundDraftsAndPreservesPartiallyBoundAgreement()
    {
        var (preparationSeeder, preparationLegalDocuments) = BuildSeeder(_ctx.Db);
        preparationLegalDocuments.FailUploadAttempt = 1;
        await FluentActions.Awaiting(() => preparationSeeder.SeedPortfolioAsync(
                1, "prepare-mixed-legal-reconcile", CancellationToken.None))
            .Should().ThrowAsync<IOException>();

        var partiallyBoundAgreementId = await _ctx.Db.LeaseAgreements.AsNoTracking()
            .Where(candidate => candidate.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && candidate.LeaseManagement!.RelationshipNumber != "DEMO-LM-ACTIVE-017")
            .OrderBy(candidate => candidate.AgreementNumber)
            .Select(candidate => candidate.Id)
            .FirstAsync();
        var unboundAgreementIds = await _ctx.Db.LeaseAgreements
            .Where(candidate => candidate.PortfolioId == 1
                && candidate.AgreementNumber.StartsWith("DEMO-AGR-ACTIVE-")
                && candidate.LeaseManagement!.RelationshipNumber != "DEMO-LM-ACTIVE-017"
                && candidate.Id != partiallyBoundAgreementId
                && candidate.IssuedArtifactId == null
                && candidate.ExecutedArtifactId == null
                && candidate.FullyExecutedAtUtc == null)
            .Select(candidate => candidate.Id)
            .ToListAsync();
        unboundAgreementIds.Should().NotBeEmpty();

        var (reconcileSeeder, reconcileLegalDocuments) = BuildSeeder(_ctx.Db);
        LegalDocumentArtifact? issuedArtifact = null;
        DateTime? issuedAtUtc = null;
        reconcileLegalDocuments.BeforeUploadAttemptAsync = async attempt =>
        {
            if (attempt != 2)
            {
                return;
            }

            var agreement = await _ctx.Db.LeaseAgreements
                .SingleAsync(candidate => candidate.Id == partiallyBoundAgreementId);
            issuedArtifact = await BindRealIssuedArtifactAsync(_ctx.Db, agreement);
            issuedAtUtc = agreement.IssuedAtUtc;
            _ctx.Db.ChangeTracker.Clear();
        };
        var reconcile = () => reconcileSeeder.SeedPortfolioAsync(
            1, "reconcile-mixed-legal-bindings", CancellationToken.None);

        await reconcile.Should().NotThrowAsync();

        issuedArtifact.Should().NotBeNull();
        _ctx.Db.ChangeTracker.Clear();
        var preservedAgreement = await _ctx.Db.LeaseAgreements.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == partiallyBoundAgreementId);
        preservedAgreement.IssuedArtifactId.Should().Be(issuedArtifact!.Id);
        preservedAgreement.IssuedAtUtc.Should().Be(issuedAtUtc);
        preservedAgreement.ExecutedArtifactId.Should().BeNull();
        preservedAgreement.FullyExecutedAtUtc.Should().BeNull();
        (await _ctx.Db.LeaseAgreements.CountAsync(candidate =>
            unboundAgreementIds.Contains(candidate.Id)
            && candidate.IssuedArtifactId != null
            && candidate.ExecutedArtifactId != null
            && candidate.FullyExecutedAtUtc != null)).Should().Be(unboundAgreementIds.Count);
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
            _ctx.Db,
            new UnexpectedRequestWriteExecutor());
    }

    private sealed class UnexpectedRequestWriteExecutor : IWriteExecutor
    {
        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey, TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData where TResult : notnull =>
            throw new InvalidOperationException("A provider write was not expected.");
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
        var (atomic, atomicContext, _, atomicDb) = BuildAtomicServices(db, interceptors);
        var legalDocuments = new DemoLegalTestDependencies(atomicDb);
        return (new DemoDataSeeder(
            atomicDb,
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

    private static AtomicCommandReceipt FrozenReceipt(
        string operation, string key, string fingerprint, string contract, string resultJson) => new()
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = operation,
            IdempotencyKey = key,
            RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = contract,
            ResultJson = resultJson,
            StartedAt = DateTime.UnixEpoch,
            CompletedAt = DateTime.UnixEpoch,
        };

    private (IWriteExecutor Writes, IAtomicCommandContext Context, IPendingFileUploadStore PendingUploads, RentalCommandDbContext Db)
        BuildAtomicServices(
        RentalCommandDbContext db,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, DemoSeedTestActor>();
        services.AddAtomicPersistenceKernel();
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
            serviceProvider.GetRequiredService<IWriteExecutor>(),
            serviceProvider.GetRequiredService<IAtomicCommandContext>(),
            serviceProvider.GetRequiredService<IPendingFileUploadStore>(),
            serviceProvider.GetRequiredService<RentalCommandDbContext>());
    }

    private sealed class DemoSeedTestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "test:demo-seed";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class FixedDemoTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
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

    private static async Task<LegalDocumentArtifact> BindRealIssuedArtifactAsync(
        RentalCommandDbContext db,
        LeaseAgreement agreement)
    {
        var issuedAtUtc = DateTime.UtcNow;
        var fileName = $"real-issued-{agreement.Id}.pdf";
        var storedFile = new StoredFile
        {
            PortfolioId = agreement.PortfolioId,
            FileName = fileName,
            FilePath = $"real-esign/{agreement.PortfolioId}/{Guid.NewGuid():N}/{fileName}",
            ContentType = "application/pdf",
            FileSize = 1024,
            ContentSha256 = new string('a', 64),
            EntityType = nameof(LeaseAgreement),
            EntityId = agreement.Id,
            UploadedAt = issuedAtUtc,
        };
        db.StoredFiles.Add(storedFile);
        await db.SaveChangesAsync();

        var artifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = agreement.PortfolioId,
            StoredFileId = storedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = storedFile.FilePath,
            FileName = storedFile.FileName,
            ContentType = storedFile.ContentType,
            ByteLength = storedFile.FileSize,
            ContentSha256 = storedFile.ContentSha256,
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = issuedAtUtc,
            CreatedByUserId = 1,
        };
        db.LegalDocumentArtifacts.Add(artifact);
        await db.SaveChangesAsync();

        agreement.IssuedArtifactId = artifact.Id;
        agreement.IssuedAtUtc = issuedAtUtc;
        agreement.ExecutedArtifactId = null;
        agreement.FullyExecutedAtUtc = null;
        await db.SaveChangesAsync();
        return artifact;
    }
}

internal sealed class DemoPortfolioDiscoveryCommandInterceptor : DbCommandInterceptor
{
    public bool UsedFunction { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        if (command.CommandText.Contains(
                "public.rc_demo_seed_portfolio_ids()",
                StringComparison.Ordinal))
        {
            UsedFunction = true;
        }
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
    public Func<int, Task>? BeforeUploadAttemptAsync { get; set; }
    public IReadOnlyDictionary<string, byte[]> StoredBytes => _stored;
    public Dictionary<string, PendingFileUploadAdmission> AdmissionsByPurpose { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, PendingFileUploadAdmission> AdmissionsByOperationId { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, string> OperationIdsByPurpose { get; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, Guid> AdmissionIdsByPurpose { get; } =
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
                Id = AdmissionIdsByPurpose.GetValueOrDefault(purpose, Guid.NewGuid()),
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
        if (BeforeUploadAttemptAsync is not null)
            await BeforeUploadAttemptAsync(_uploadAttempt);
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
