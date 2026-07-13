using FluentAssertions;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Focused Unit Command Center tests against the migrated PostgreSQL schema and its canonical
/// LeaseManagement, Agreement, Party, Account, ledger, and operational-period read models.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class UnitDashboardServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _executedSql = [];
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private UnitDashboardService _sut = null!;

    public UnitDashboardServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_executedSql)]);
        _db = _ctx.Db;
        _sut = new UnitDashboardService(_db, new AuditDescriber(), new AuditDiffBuilder(), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task GetTimelineAsync_ScopesCanonicalChildAuditRowsInSqlWithoutPreloadingIds()
    {
        var seeded = SeedUnitWithTimelineChildren();
        _db.AuditLogs.AddRange(
            Audit(nameof(Unit), seeded.Unit.Id, 8),
            Audit(nameof(LeaseManagement), seeded.Relationship.Id, 7),
            Audit(nameof(LeaseAgreement), seeded.Agreement.Id, 6),
            Audit(nameof(TenantAccount), seeded.Account.Id, 5),
            Audit(nameof(WorkOrder), seeded.WorkOrder.Id, 4),
            Audit(nameof(Expense), seeded.Expense.Id, 3),
            Audit(nameof(LeaseManagement), seeded.ForeignRelationship.Id, 2));
        _db.SaveChanges();

        _executedSql.Clear();

        var rows = await _sut.GetTimelineAsync(PortfolioId, seeded.Unit.Id, 0, 10, CancellationToken.None);

        rows.Select(row => row.EntityType).Should().BeEquivalentTo([
            nameof(Unit), nameof(LeaseManagement), nameof(LeaseAgreement), nameof(TenantAccount),
            nameof(WorkOrder), nameof(Expense),
        ]);
        rows.Should().NotContain(row => row.EntityType == nameof(LeaseManagement)
            && row.EntityId == seeded.ForeignRelationship.Id);

        _executedSql.Should().NotContain(command => IsChildIdPreload(command),
            "canonical child scopes must remain correlated subqueries inside the paged audit query");

        var auditSql = _executedSql.Single(command => command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase));
        auditSql.Should().Contain("ORDER BY");
        auditSql.Should().Contain("LIMIT");
        auditSql.Should().Contain("LeaseManagements");
        auditSql.Should().Contain("LeaseAgreements");
        auditSql.Should().Contain("TenantAccounts");
        auditSql.Should().Contain("WorkOrders");
        auditSql.Should().Contain("Expenses");
        auditSql.Should().NotContain("FROM \"Leases\"");
        auditSql.Should().NotContain("FROM \"Payments\"");
    }

    [Fact]
    public async Task GetDashboardAsync_IncludesExpenseAndCanonicalLedgerSourceDocuments()
    {
        var seeded = SeedUnitWithTimelineChildren();
        var now = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var directExpense = ExpenseFor(seeded.Unit, seeded.Property, "Direct unit receipt", 55m, now);
        var foreignExpense = ExpenseFor(seeded.ForeignUnit, seeded.Property, "Other unit receipt", 75m, now);
        _db.Expenses.AddRange(directExpense, foreignExpense);
        _db.SaveChanges();

        var workOrderReceipt = File(nameof(Expense), seeded.Expense.Id, "work-order-receipt.pdf", now);
        var directReceipt = File(nameof(Expense), directExpense.Id, "direct-unit-receipt.png", now.AddMinutes(-1));
        var foreignReceipt = File(nameof(Expense), foreignExpense.Id, "foreign-unit-receipt.pdf", now.AddMinutes(1));
        var rentCheck = File(nameof(TenantLedgerEntry), seeded.Account.Id, "rent-check.png", now.AddMinutes(-2));
        _db.StoredFiles.AddRange(workOrderReceipt, directReceipt, foreignReceipt, rentCheck);
        _db.SaveChanges();

        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = seeded.Account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1200m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Rent check",
            BusinessKey = "dashboard-doc-rent-check",
            SourceStoredFileId = rentCheck.Id,
            CreatedByUserId = ActorUserId,
        });
        _db.SaveChanges();

        _executedSql.Clear();
        var dashboard = await _sut.GetDashboardAsync(PortfolioId, seeded.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.Header.DocsNeedingReviewCount.Should().Be(5);
        dashboard.Overview.PendingDocs.Select(document => document.FileName)
            .Should().BeEquivalentTo([
                $"agreement-{seeded.Relationship.Id}-issued.pdf",
                $"agreement-{seeded.Relationship.Id}-executed.pdf",
                "work-order-receipt.pdf",
                "direct-unit-receipt.png",
                "rent-check.png",
            ]);
        dashboard.Overview.PendingDocs.Should().NotContain(document => document.FileName == "foreign-unit-receipt.pdf");

        var documentSql = _executedSql
            .Where(command => command.Contains("FROM \"StoredFiles\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        documentSql.Should().NotBeEmpty();
        documentSql.Should().OnlyContain(command =>
            command.Contains("Expenses", StringComparison.OrdinalIgnoreCase)
            && command.Contains("TenantLedgerEntries", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDashboardAsync_ReturnsTurnoverSummaryFromSqlAggregatesAndOperationalPeriod()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedPropertyUnit("Turnover Flats", "7", 1400m, now);
        SeedOperationalState(unit, UnitOperationalPeriodType.Turnover, now.AddDays(-6));

        var openWork = WorkOrderFor(unit, property, "Paint bedrooms", WorkOrderStatus.InProgress,
            now.AddDays(-4), estimatedCost: 200m, scheduledFor: now.AddDays(3));
        var completedWork = WorkOrderFor(unit, property, "Trash-out", WorkOrderStatus.Completed,
            now.AddDays(-6), estimatedCost: 100m, actualCost: 120m, completedAt: now.AddDays(-2));
        var linkedReceipt = ExpenseFor(unit, property, "Paint supplies", 75m, now.AddDays(-1), openWork);
        var directReceipt = ExpenseFor(unit, property, "Key copies", 25m, now.AddDays(-3));
        var (_, foreignUnit) = SeedPropertyUnit("Other Building", "8", 900m, now);
        var foreignReceipt = ExpenseFor(foreignUnit, foreignUnit.Property!, "Other unit", 999m, now);
        _db.AddRange(openWork, completedWork, linkedReceipt, directReceipt, foreignReceipt);
        _db.SaveChanges();

        _executedSql.Clear();
        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be("Turnover");
        dashboard.Turnover.Status.Should().Be("InProgress");
        dashboard.Turnover.TotalTaskCount.Should().Be(2);
        dashboard.Turnover.OpenTaskCount.Should().Be(1);
        dashboard.Turnover.CompletedTaskCount.Should().Be(1);
        dashboard.Turnover.ReceiptCount.Should().Be(2);
        dashboard.Turnover.EstimatedCost.Should().Be(300m);
        dashboard.Turnover.ActualCost.Should().Be(220m);
        dashboard.Turnover.TargetReadyDate.Should().Be(openWork.ScheduledFor);

        _executedSql.Should().Contain(command => command.Contains("FROM \"WorkOrders\"")
            && command.Contains("GROUP BY") && command.Contains("SUM"));
        _executedSql.Should().Contain(command => command.Contains("FROM \"Expenses\"")
            && command.Contains("GROUP BY") && command.Contains("SUM"));
    }

    [Fact]
    public async Task GetDashboardAsync_LinksReadyNextActionToUnitApplicationLinkFlow()
    {
        var now = DateTime.UtcNow;
        var (_, unit) = SeedPropertyUnit("Oak Ridge", "3B", 975m, now);

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be("Ready");
        dashboard.NextBestAction.Label.Should().Be("List this unit");
        dashboard.NextBestAction.Href.Should().Be($"/units/{unit.Id}?tab=listing");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksRenewalNextActionToTenantRenewalNoticeFlow()
    {
        var now = DateTime.UtcNow;
        var graph = SeedCanonicalRelationship("Oak Ridge", "3B", "Riley", "Tenant", 975m,
            now, possessionGivenAtUtc: now.AddMonths(-10), agreementEndOn: DateOnly.FromDateTime(now.AddDays(44)));

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, graph.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be("Renewal");
        dashboard.NextBestAction.Label.Should().StartWith("Prepare renewal");
        dashboard.NextBestAction.Href.Should()
            .Be($"/tenants/{graph.Tenant.Id}?action=create-notice&noticeType=lease-renewal-offer");
    }

    [Fact]
    public async Task GetDashboardAsync_LinksPlannedPossessionToConcreteMoveInWorkflow()
    {
        var now = DateTime.UtcNow;
        var graph = SeedCanonicalRelationship("Maple Heights", "2A", "Morgan", "Movein", 1200m,
            now, plannedPossessionAtUtc: now.AddDays(3));

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, graph.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be("MoveIn");
        dashboard.NextBestAction.Label.Should().Be("Confirm possession / collect deposit");
        dashboard.NextBestAction.Href.Should()
            .Be($"/units/{graph.Unit.Id}?tab=lease&action=confirm-move-in");
    }

    [Fact]
    public async Task GetDashboardAsync_PossessionGivenResolvesToActive()
    {
        var now = DateTime.UtcNow;
        var graph = SeedCanonicalRelationship("Maple Heights", "2A", "Morgan", "Movein", 1200m,
            now, possessionGivenAtUtc: now.AddDays(-5));

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, graph.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.LifecycleStage.Should().Be("Active");
        dashboard.NextBestAction.Label.Should().Be("Rent on track");
        dashboard.Header.CurrentTenantName.Should().Be("Morgan Movein");
    }

    [Fact]
    public async Task GetDashboardAsync_ReadsOutstandingFromCurrentTenantAccountBalanceProjection()
    {
        var now = DateTime.UtcNow;
        var graph = SeedCanonicalRelationship("Birch Lane", "101", "Quincy", "Tenant", 1000m,
            now, possessionGivenAtUtc: now.AddMonths(-1), receivableBalance: 300m, pastDueAmount: 300m);

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, graph.Unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.CurrentLease!.TenantAccountId.Should().Be(graph.Account.Id);
        dashboard.Header.OutstandingRentBalance.Should().Be(300m);
        dashboard.Header.RentState.Should().Be("Overdue");
    }

    [Fact]
    public async Task GetDashboardAsync_UsesOnlySelectedCurrentRelationshipAccountBalance()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedPropertyUnit("Cedar Court", "5C", 1000m, now);
        var prior = SeedHistoricalRelationship(property, unit, "Pat", "Former", 950m, now.AddMonths(-18), now.AddMonths(-6));
        SeedBalance(prior, 1000m, 1000m, DateOnly.FromDateTime(now.AddMonths(-7)));
        var current = SeedRelationshipOnExistingUnit(property, unit, "Pat", "Current", 1000m, now,
            possessionGivenAtUtc: now.AddMonths(-1), agreementEndOn: DateOnly.FromDateTime(now.AddYears(1)),
            receivableBalance: 500m, nextDueOn: DateOnly.FromDateTime(now.AddDays(10)));

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.CurrentLease!.LeaseManagementId.Should().Be(current.Relationship.Id);
        dashboard.CurrentLease.TenantAccountId.Should().Be(current.Account.Id);
        dashboard.Header.OutstandingRentBalance.Should().Be(500m);
        dashboard.Header.RentState.Should().Be("Due");
    }

    [Fact]
    public async Task GetDashboardAsync_DoesNotPromoteEndedRelationshipToCurrentLease()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedPropertyUnit("Historical Flats", "2", 1100m, now);
        var ended = SeedHistoricalRelationship(property, unit, "Former", "Resident", 1100m,
            now.AddYears(-1), now.AddMonths(-1));
        SeedBalance(ended, 1100m, 1100m, DateOnly.FromDateTime(now.AddMonths(-2)));

        var dashboard = await _sut.GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.CurrentLease.Should().BeNull();
        dashboard.CurrentTenant.Should().BeNull();
        dashboard.CurrentTenants.Should().BeEmpty();
        dashboard.Header.RentState.Should().Be("NoLease");
        dashboard.Header.OutstandingRentBalance.Should().Be(0m);
    }

    private CanonicalGraph SeedCanonicalRelationship(
        string propertyName,
        string unitNumber,
        string firstName,
        string lastName,
        decimal rent,
        DateTime now,
        DateTime? possessionGivenAtUtc = null,
        DateTime? plannedPossessionAtUtc = null,
        DateOnly? agreementEndOn = null,
        decimal receivableBalance = 0m,
        decimal pastDueAmount = 0m,
        DateOnly? nextDueOn = null)
    {
        var (property, unit) = SeedPropertyUnit(propertyName, unitNumber, rent, now);
        return SeedRelationshipOnExistingUnit(property, unit, firstName, lastName, rent, now,
            possessionGivenAtUtc, plannedPossessionAtUtc, agreementEndOn, receivableBalance,
            pastDueAmount, nextDueOn);
    }

    private CanonicalGraph SeedRelationshipOnExistingUnit(
        Property property,
        Unit unit,
        string firstName,
        string lastName,
        decimal rent,
        DateTime now,
        DateTime? possessionGivenAtUtc = null,
        DateTime? plannedPossessionAtUtc = null,
        DateOnly? agreementEndOn = null,
        decimal receivableBalance = 0m,
        decimal pastDueAmount = 0m,
        DateOnly? nextDueOn = null)
    {
        var tenant = Tenant(firstName, lastName, now);
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var relationship = Relationship(property, unit, now, plannedPossessionAtUtc, possessionGivenAtUtc);
        _db.LeaseManagements.Add(relationship);
        _db.SaveChanges();

        var (issuedArtifact, executedArtifact) = SeedAgreementArtifacts(relationship.Id, now);
        var startOn = DateOnly.FromDateTime(possessionGivenAtUtc ?? plannedPossessionAtUtc ?? now);
        var agreement = Agreement(
            relationship,
            rent,
            startOn,
            agreementEndOn ?? startOn.AddYears(1),
            issuedArtifact.Id,
            executedArtifact.Id,
            now);
        var party = Party(relationship, tenant, startOn, now);
        var account = Account(relationship, now);
        _db.AddRange(agreement, party, account);
        _db.SaveChanges();

        var graph = new CanonicalGraph(property, unit, tenant, relationship, agreement, account);
        if (receivableBalance != 0m || pastDueAmount != 0m)
        {
            SeedBalance(graph, receivableBalance, pastDueAmount, nextDueOn);
        }

        return graph;
    }

    private CanonicalGraph SeedHistoricalRelationship(
        Property property,
        Unit unit,
        string firstName,
        string lastName,
        decimal rent,
        DateTime possessionGivenAtUtc,
        DateTime possessionReturnedAtUtc)
    {
        var tenant = Tenant(firstName, lastName, possessionGivenAtUtc);
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        var relationship = Relationship(property, unit, possessionGivenAtUtc, possessionGivenAtUtc, possessionGivenAtUtc);
        relationship.PossessionReturnedAtUtc = possessionReturnedAtUtc;
        relationship.AccountClosedAtUtc = possessionReturnedAtUtc;
        _db.LeaseManagements.Add(relationship);
        _db.SaveChanges();
        var (issuedArtifact, executedArtifact) = SeedAgreementArtifacts(relationship.Id, possessionGivenAtUtc);
        var startOn = DateOnly.FromDateTime(possessionGivenAtUtc);
        var agreement = Agreement(
            relationship,
            rent,
            startOn,
            DateOnly.FromDateTime(possessionReturnedAtUtc),
            issuedArtifact.Id,
            executedArtifact.Id,
            possessionGivenAtUtc);
        var party = Party(relationship, tenant, startOn, possessionGivenAtUtc);
        party.EffectiveThrough = DateOnly.FromDateTime(possessionReturnedAtUtc);
        var account = Account(relationship, possessionGivenAtUtc);
        account.ClosedAtUtc = possessionReturnedAtUtc;
        _db.AddRange(agreement, party, account);
        _db.SaveChanges();
        return new CanonicalGraph(property, unit, tenant, relationship, agreement, account);
    }

    private (LegalDocumentArtifact Issued, LegalDocumentArtifact Executed) SeedAgreementArtifacts(
        int relationshipId,
        DateTime now)
    {
        var issuedFile = File(null, null, $"agreement-{relationshipId}-issued.pdf", now);
        var executedFile = File(null, null, $"agreement-{relationshipId}-executed.pdf", now);
        _db.StoredFiles.AddRange(issuedFile, executedFile);
        _db.SaveChanges();

        var issuedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = issuedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.IssuedAgreement,
            StorageKey = issuedFile.FilePath,
            FileName = issuedFile.FileName,
            ContentType = issuedFile.ContentType,
            ByteLength = issuedFile.FileSize,
            ContentSha256 = new string('a', 64),
            LegalIssuanceFingerprint = new string('b', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        var executedArtifact = new LegalDocumentArtifact
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            StoredFileId = executedFile.Id,
            ArtifactKind = LegalDocumentArtifactKind.ExecutedAgreement,
            StorageKey = executedFile.FilePath,
            FileName = executedFile.FileName,
            ContentType = executedFile.ContentType,
            ByteLength = executedFile.FileSize,
            ContentSha256 = new string('c', 64),
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        };
        _db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        _db.SaveChanges();
        return (issuedArtifact, executedArtifact);
    }

    private void SeedBalance(CanonicalGraph graph, decimal receivable, decimal pastDue, DateOnly? nextDueOn)
    {
        if (pastDue > 0m)
        {
            AddLedgerEntry(
                graph.Account,
                TenantLedgerEntryType.RentCharge,
                TenantLedgerDirection.Debit,
                pastDue,
                $"dashboard-past-due-{Guid.NewGuid():N}",
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        }

        var remainingReceivable = receivable - pastDue;
        if (remainingReceivable > 0m)
        {
            AddLedgerEntry(
                graph.Account,
                TenantLedgerEntryType.RentCharge,
                TenantLedgerDirection.Debit,
                remainingReceivable,
                $"dashboard-current-due-{Guid.NewGuid():N}",
                nextDueOn ?? DateOnly.FromDateTime(DateTime.UtcNow));
        }

        _db.SaveChanges();
    }

    private (Property Property, Unit Unit) SeedPropertyUnit(string propertyName, string unitNumber, decimal rent, DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = $"{unitNumber} Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = unitNumber,
            MarketRent = rent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit);
        _db.SaveChanges();
        return (property, unit);
    }

    private void SeedOperationalState(Unit unit, UnitOperationalPeriodType type, DateTime startedAtUtc)
    {
        _db.UnitOperationalPeriods.Add(new UnitOperationalPeriod
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Type = type,
            StartedAtUtc = startedAtUtc,
            Reason = "Canonical dashboard fixture",
            CreatedAtUtc = startedAtUtc,
            CreatedByUserId = ActorUserId,
        });
        _db.SaveChanges();
    }

    private SeededTimelineGraph SeedUnitWithTimelineChildren()
    {
        var now = DateTime.UtcNow;
        var graph = SeedCanonicalRelationship("Maple", "1A", "Maria", "Tenant", 1200m,
            now, possessionGivenAtUtc: now.AddMonths(-1));
        var workOrder = WorkOrderFor(graph.Unit, graph.Property, "Fix sink", WorkOrderStatus.InProgress, now);
        var expense = ExpenseFor(graph.Unit, graph.Property, "Parts", 40m, now, workOrder);
        var (foreignProperty, foreignUnit) = SeedPropertyUnit("Foreign Maple", "9Z", 900m, now);
        var foreign = SeedRelationshipOnExistingUnit(foreignProperty, foreignUnit, "Other", "Tenant", 900m,
            now, possessionGivenAtUtc: now.AddMonths(-1));
        _db.AddRange(workOrder, expense);
        _db.SaveChanges();
        return new SeededTimelineGraph(
            graph.Property, graph.Unit, graph.Relationship, graph.Agreement, graph.Account,
            workOrder, expense, foreignUnit, foreign.Relationship);
    }

    private static Tenant Tenant(string firstName, string lastName, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FirstName = firstName,
        LastName = lastName,
        Email = $"{firstName}.{lastName}@example.test".ToLowerInvariant(),
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagement Relationship(
        Property property,
        Unit unit,
        DateTime now,
        DateTime? plannedPossessionAtUtc,
        DateTime? possessionGivenAtUtc) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        PropertyId = property.Id,
        UnitId = unit.Id,
        RelationshipNumber = $"REL-{Guid.NewGuid():N}"[..12],
        PlannedPossessionAtUtc = plannedPossessionAtUtc ?? possessionGivenAtUtc,
        PossessionGivenAtUtc = possessionGivenAtUtc,
        CreatedAtUtc = now,
        CreatedByUserId = ActorUserId,
        UpdatedAtUtc = now,
        RowVersion = Guid.NewGuid(),
    };

    private static LeaseAgreement Agreement(
        LeaseManagement relationship,
        decimal rent,
        DateOnly startOn,
        DateOnly endOn,
        int issuedArtifactId,
        int executedArtifactId,
        DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        LeaseManagementId = relationship.Id,
        VersionNumber = 1,
        AgreementNumber = $"AGR-{Guid.NewGuid():N}"[..12],
        ChangeType = LeaseAgreementChangeType.Initial,
        TermType = LeaseAgreementTermType.FixedTerm,
        TermStartOn = startOn,
        TermEndOn = endOn,
        GoverningFromOn = startOn,
        BaseRentAmount = rent,
        RentDueDay = 1,
        SecurityDepositObligation = rent,
        LateFeeAmount = 50m,
        GracePeriodDays = 5,
        Currency = "USD",
        TermsSchemaVersion = 1,
        TermsPayload = "{}",
        DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
            PortfolioId, ActorUserId, now),
        IssuedArtifactId = issuedArtifactId,
        IssuedAtUtc = now,
        ExecutedArtifactId = executedArtifactId,
        FullyExecutedAtUtc = now,
        CreatedAtUtc = now,
        CreatedByUserId = ActorUserId,
        UpdatedAtUtc = now,
    };

    private static LeaseManagementParty Party(
        LeaseManagement relationship,
        Tenant tenant,
        DateOnly effectiveFrom,
        DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        LeaseManagementId = relationship.Id,
        TenantId = tenant.Id,
        Role = LeaseManagementPartyRole.PrimaryTenant,
        EffectiveFrom = effectiveFrom,
        ChangeReason = "Canonical dashboard fixture",
        CreatedAtUtc = now,
        CreatedByUserId = ActorUserId,
    };

    private static TenantAccount Account(LeaseManagement relationship, DateTime now) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        LeaseManagementId = relationship.Id,
        AccountNumber = $"TA-{Guid.NewGuid():N}"[..12],
        Currency = "USD",
        OpenedAtUtc = now,
        CreatedAtUtc = now,
        CreatedByUserId = ActorUserId,
    };

    private void AddLedgerEntry(
        TenantAccount account,
        TenantLedgerEntryType type,
        TenantLedgerDirection direction,
        decimal amount,
        string businessKey,
        DateOnly effectiveOn)
    {
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = type,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = effectiveOn,
            DueOn = direction == TenantLedgerDirection.Debit ? effectiveOn : null,
            PostedAtUtc = effectiveOn.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            Description = businessKey,
            BusinessKey = businessKey,
            CreatedByUserId = ActorUserId,
        });
    }

    private static WorkOrder WorkOrderFor(
        Unit unit,
        Property property,
        string title,
        WorkOrderStatus status,
        DateTime requestedAt,
        decimal? estimatedCost = null,
        decimal? actualCost = null,
        DateTime? scheduledFor = null,
        DateTime? completedAt = null) => new()
    {
        PortfolioId = PortfolioId,
        PropertyId = property.Id,
        UnitId = unit.Id,
        Title = title,
        Description = title,
        Status = status,
        EstimatedCost = estimatedCost,
        ActualCost = actualCost,
        RequestedAt = requestedAt,
        ScheduledFor = scheduledFor,
        CompletedAt = completedAt,
        UpdatedAt = completedAt ?? requestedAt,
    };

    private static Expense ExpenseFor(
        Unit unit,
        Property property,
        string description,
        decimal amount,
        DateTime incurredAt,
        WorkOrder? workOrder = null) => new()
    {
        PortfolioId = PortfolioId,
        PropertyId = property.Id,
        UnitId = unit.Id,
        WorkOrder = workOrder,
        Description = description,
        Amount = amount,
        IncurredAt = incurredAt,
        CreatedAt = incurredAt,
        UpdatedAt = incurredAt,
    };

    private AuditLog Audit(string entityType, int entityId, int minutesAgo) => new()
    {
        PortfolioId = PortfolioId,
        EntityType = entityType,
        EntityId = entityId,
        Operation = AuditLogOperation.Created,
        ActorLabel = "test",
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
    };

    private static StoredFile File(string? entityType, long? entityId, string fileName, DateTime uploadedAt) => new()
    {
        PortfolioId = PortfolioId,
        EntityType = entityType,
        EntityId = entityId,
        FileName = fileName,
        FilePath = fileName,
        ContentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/pdf",
        FileSize = 1024,
        UploadedAt = uploadedAt,
    };

    private static bool IsChildIdPreload(string command) =>
        IsBareIdSelect(command, "LeaseManagements")
        || IsBareIdSelect(command, "LeaseAgreements")
        || IsBareIdSelect(command, "TenantAccounts")
        || IsBareIdSelect(command, "WorkOrders")
        || IsBareIdSelect(command, "Inspections")
        || IsBareIdSelect(command, "Appointments")
        || IsBareIdSelect(command, "Expenses");

    private static bool IsBareIdSelect(string command, string table) =>
        command.Contains("SELECT \"", StringComparison.OrdinalIgnoreCase)
        && command.Contains("\".\"Id\"", StringComparison.OrdinalIgnoreCase)
        && command.Contains($"FROM \"{table}\"", StringComparison.OrdinalIgnoreCase)
        && !command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase)
        && !command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase);

    private sealed record CanonicalGraph(
        Property Property,
        Unit Unit,
        Tenant Tenant,
        LeaseManagement Relationship,
        LeaseAgreement Agreement,
        TenantAccount Account);

    private sealed record SeededTimelineGraph(
        Property Property,
        Unit Unit,
        LeaseManagement Relationship,
        LeaseAgreement Agreement,
        TenantAccount Account,
        WorkOrder WorkOrder,
        Expense Expense,
        Unit ForeignUnit,
        LeaseManagement ForeignRelationship);

}
