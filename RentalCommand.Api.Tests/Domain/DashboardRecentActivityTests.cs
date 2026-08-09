using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Pins the enriched dashboard "Recent Activity" feed: every row must carry the touched entity's
/// <c>EntityId</c> (for deep-linking) and a human <c>Label</c> naming the specific record, and the
/// labels must be resolved from one page-keyed entity-fact statement after the authorized, stably
/// ordered ten-row audit page. No entity branch may scan beyond the keys in that bounded page.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class DashboardRecentActivityTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _executedSql = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommand.Data.RentalCommandDbContext _db = null!;
    private DashboardService _sut = null!;
    private WorkspaceReadScope _scope;

    public DashboardRecentActivityTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_executedSql)]);
        _db = _context.Db;
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(DashboardRecentActivityTests));
        await _context.ActivateApiScopeAsync(_scope);
        _sut = new DashboardService(_db, new AuditDescriber(), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task RecentActivity_NamesEachEntityAndCarriesEntityId()
    {
        var seeded = SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        var byKey = dashboard!.RecentActivity.ToDictionary(r => (r.Type, r.EntityId));

        byKey[("Tenant", seeded.Tenant1.Id)].Label.Should().Be("Maria Tenant");
        byKey[("Tenant", seeded.Tenant2.Id)].Label.Should().Be("Liam Renter");
        byKey[("Tenant", seeded.Tenant3.Id)].Label.Should().Be("Noah Lessee");
        byKey[("Unit", seeded.Unit.Id)].Label.Should().Be("Maple · Unit 1A");
        byKey[("LeaseManagement", seeded.Relationship.Id)].Label.Should().Be("REL-1A");
        byKey[("LeaseManagement", seeded.Relationship.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("WorkOrder", seeded.WorkOrder.Id)].Label.Should().Be("Fix sink");
        byKey[("WorkOrder", seeded.WorkOrder.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("Property", seeded.Property.Id)].Label.Should().Be("Maple");
        byKey[("TenantAccount", seeded.Account.Id)].Label.Should().Be("TA-1A");
        byKey[("TenantAccount", seeded.Account.Id)].UnitId.Should().Be(seeded.Unit.Id);
        byKey[("Expense", seeded.Expense.Id)].Label.Should().Be("Plumbing parts");
        byKey[("Expense", seeded.Expense.Id)].UnitId.Should().Be(seeded.Unit.Id);

        // Every row still carries the touched entity's id so the web can deep-link to it.
        dashboard.RecentActivity.Should().OnlyContain(r => r.EntityId > 0);
    }

    [Fact]
    public async Task RecentActivity_ResolvesLabelsAndUnitIdsForRemainingSupportedTypes()
    {
        var seeded = SeedActivityGraph();
        var additional = SeedRemainingActivityEntities(seeded);

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        var byKey = dashboard!.RecentActivity.ToDictionary(row => (row.Type, row.EntityId));
        byKey[(nameof(LeaseAgreement), additional.Agreement.Id)].Should().Match<DashboardActivity>(row =>
            row.Label == "AGR-ACTIVITY" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(LeaseAddendum), additional.Addendum.Id)].Should().Match<DashboardActivity>(row =>
            row.Label == "ADD-ACTIVITY" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(TenantLedgerEntry), checked((int)additional.LedgerEntry.Id))]
            .Should().Match<DashboardActivity>(row =>
                row.Label == "Activity ledger entry" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(SecurityDepositAccount), additional.DepositAccount.Id)]
            .Should().Match<DashboardActivity>(row =>
                row.Label == "TA-1A deposit" && row.UnitId == seeded.Unit.Id);
        byKey[("SecurityDeposit", additional.DepositAccount.Id)]
            .Should().Match<DashboardActivity>(row =>
                row.Label == "TA-1A deposit" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(SecurityDepositEntry), checked((int)additional.DepositEntry.Id))]
            .Should().Match<DashboardActivity>(row =>
                row.Label == "Activity deposit entry" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(Appointment), additional.Appointment.Id)].Should().Match<DashboardActivity>(row =>
            row.Label == "Activity appointment" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(Inspection), additional.Inspection.Id)].Should().Match<DashboardActivity>(row =>
            row.Label == "Maple" && row.UnitId == seeded.Unit.Id);
        byKey[(nameof(RentalApplication), additional.Application.Id)].Should().Match<DashboardActivity>(row =>
            row.Label == "Avery Applicant" && row.UnitId == seeded.Unit.Id);
    }

    [Fact]
    public async Task RecentActivity_WidensBigintLedgerAndDepositLookupIds()
    {
        var seeded = SeedActivityGraph();
        var baseTime = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = seeded.Relationship.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-BIGINT",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(baseTime),
            TermEndOn = DateOnly.FromDateTime(baseTime.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(baseTime),
            BaseRentAmount = 1_200m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, baseTime),
            CreatedAtUtc = baseTime,
            CreatedByUserId = 1,
            UpdatedAtUtc = baseTime,
        };
        _db.LeaseAgreements.Add(agreement);
        _db.SaveChanges();

        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = seeded.Account.Id,
            OriginatingAgreementId = agreement.Id,
            Currency = "USD",
            CreatedAtUtc = baseTime,
            CreatedByUserId = 1,
        };
        _db.SecurityDepositAccounts.Add(depositAccount);
        _db.SaveChanges();

        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            Id = (long)int.MaxValue + 1,
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = seeded.Account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 1_200m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(baseTime),
            PostedAtUtc = baseTime,
            Description = "Bigint tenant ledger entry",
            BusinessKey = "dashboard-bigint-ledger",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = 1,
        });
        _db.SecurityDepositEntries.Add(new SecurityDepositEntry
        {
            Id = (long)int.MaxValue + 2,
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SecurityDepositAccountId = depositAccount.Id,
            EntryType = SecurityDepositEntryType.Receipt,
            Direction = SecurityDepositDirection.Increase,
            Amount = 1_200m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(baseTime),
            PostedAtUtc = baseTime,
            BusinessKey = "dashboard-bigint-deposit",
            Description = "Bigint security deposit entry",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = 1,
        });
        _db.SaveChanges();

        // This unrelated recent row forces the full entity UNION to execute while its label
        // remains a normal int-keyed lookup and the public response contract stays unchanged.
        _db.AtomicAuditLogs.Add(Audit(
            nameof(Property), seeded.Property.Id, AuditLogOperation.Updated, baseTime, -60));
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        dashboard!.RecentActivity.Should().Contain(row =>
            row.Type == nameof(Property)
            && row.EntityId == seeded.Property.Id
            && row.Label == "Maple");
    }

    [Fact]
    public async Task Dashboard_GroupsKpisAndPreservesKpiValues()
    {
        SeedActivityGraph();

        _executedSql.Clear();
        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        dashboard!.Occupancy.TotalUnits.Should().Be(1);
        dashboard.Occupancy.OccupiedUnits.Should().Be(0);
        dashboard.Occupancy.VacantUnits.Should().Be(1);
        dashboard.Occupancy.ReservedUnits.Should().Be(0);
        dashboard.Occupancy.OccupancyRate.Should().Be(0);
        dashboard.Maintenance.OpenCount.Should().Be(1);
        dashboard.Maintenance.EmergencyCount.Should().Be(0);
        dashboard.Maintenance.InProgressCount.Should().Be(0);
        dashboard.Leasing.TotalLeases.Should().Be(1);
        dashboard.Leasing.ActiveLeases.Should().Be(0);
        dashboard.Leasing.ByStatus.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, int>("Preparing", 1));
        dashboard.Accounting.DueThisMonthAmount.Should().Be(0m);
        dashboard.Accounting.PaidThisMonthAmount.Should().Be(0m);
        dashboard.Accounting.OverdueAmount.Should().Be(0m);
        dashboard.Accounting.ExpensesThisMonthAmount.Should().Be(0m);
        dashboard.Accounting.NetThisMonth.Should().Be(0m);

        _executedSql.Should().HaveCount(6,
            "the dashboard should use header/KPI, expiring, accounting, audit-page, page-keyed entity-fact, and appointment statements");
    }

    [Fact]
    public async Task Dashboard_Accounting_SeedsAuthorizedAccountsBeforeLedgerFacts()
    {
        SeedActivityGraph();

        _executedSql.Clear();
        await _sut.GetDashboardAsync(_scope);

        var accountingSql = _executedSql.Should().ContainSingle(command =>
            command.Contains("active_accounts AS MATERIALIZED", StringComparison.OrdinalIgnoreCase)).Subject;
        accountingSql.Should().NotContain("vw_tenant_account_balances",
            "receivable work must not expand the whole-portfolio account-balance view");
        accountingSql.Should().NotContain("vw_tenant_charge_balances",
            "the canonical charge formula must run only after the authorized active-account seed");
        accountingSql.Should().Contain(
            "entry.\"TenantAccountId\" = active_account.\"TenantAccountId\"",
            "ledger work must be keyed by the bounded authorized account seed");
        accountingSql.Should().Contain("charge_facts AS MATERIALIZED",
            "charge, reversal, and allocation facts must be combined without cross-expanding relations");
        accountingSql.Should().Contain("portfolio.\"DeletedAt\" IS NULL",
            "raw child joins must preserve the DbContext portfolio visibility filter");
        accountingSql.Should().Contain("property_row.\"DeletedAt\" IS NULL",
            "raw property reads must preserve the DbContext property visibility filter");
    }

    [Fact]
    public async Task RecentActivity_ExcludesRowsWithoutAnAuthorizedPropertyPath()
    {
        SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        dashboard!.RecentActivity.Should().NotContain(r => r.Type == "Conversation");
    }

    [Fact]
    public async Task RecentActivity_Uses_Two_PageKeyed_Statements_With_No_Label_Followups()
    {
        SeedActivityGraph();

        _executedSql.Clear();
        await _sut.GetDashboardAsync(_scope);

        var auditQueries = _executedSql
            .Where(command => command.Contains("FROM \"AtomicAuditLogs\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        auditQueries.Should().ContainSingle(
            "recent activity must fetch the authorized, stably ordered ten-row audit page first");
        auditQueries[0].Should().Contain("public.rc_api_effective_capability_scopes");
        auditQueries[0].Should().Contain("ORDER BY");
        auditQueries[0].Should().Contain("LIMIT");

        var entityFactQueries = _executedSql
            .Where(command =>
                !command.Contains("AtomicAuditLogs", StringComparison.OrdinalIgnoreCase) &&
                command.Contains("AS \"EntityType\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        entityFactQueries.Should().ContainSingle(
            "all labels and Unit ids must be resolved in one bounded entity-fact statement");
        var entityFacts = entityFactQueries[0];
        entityFacts.Should().Contain("UNION ALL");
        entityFacts.Should().Contain("= ANY",
            "each generated entity branch must carry its page-ID predicate in SQL");
        entityFacts.Should().Contain("array_agg");
        entityFacts.Should().Contain("\"EffectiveFrom\" DESC");
        entityFacts.Should().Contain("\"Id\" DESC",
            "tenant Unit context must use the latest party by EffectiveFrom and then Id");
        entityFacts.Should().NotContain("\"Appointments\"");
        entityFacts.Should().NotContain("\"Inspections\"");
        entityFacts.Should().NotContain("\"RentalApplications\"");
        entityFacts.Should().NotContain("\"SecurityDepositEntries\"",
            "entity types absent from the audit page must not generate union branches");

        _executedSql.Should().HaveCount(6,
            "recent activity contributes exactly two statements and never performs per-row lookups");
    }

    [Fact]
    public async Task RecentActivity_Uses_Stable_TopTen_Timestamp_Then_Id_Order()
    {
        var seeded = SeedActivityGraph();
        var sharedTimestamp = new DateTime(2026, 1, 11, 12, 0, 0, DateTimeKind.Utc);
        var newest = Enumerable.Range(0, 12)
            .Select(_ => Audit(
                nameof(Property),
                seeded.Property.Id,
                AuditLogOperation.Updated,
                sharedTimestamp,
                0))
            .ToList();
        _db.AtomicAuditLogs.AddRange(newest);
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().NotBeNull();
        dashboard!.RecentActivity.Should().HaveCount(10);
        dashboard.RecentActivity.Select(row => row.Id).Should().Equal(
            newest.OrderByDescending(audit => audit.Id).Take(10).Select(audit => audit.Id));
        dashboard.RecentActivity.Should().OnlyContain(row =>
            row.Type == nameof(Property) &&
            row.EntityId == seeded.Property.Id &&
            row.Label == "Maple");
    }

    [Fact]
    public async Task RecentActivity_Applies_SelectedProperty_Scope_Before_Take()
    {
        var authorized = SeedActivityGraph();
        var now = DateTime.UtcNow;
        var decoyProperty = Property("Out-of-scope decoy", now);
        var decoyUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            UnitNumber = "D1",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var decoyWorkOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = decoyProperty,
            Unit = decoyUnit,
            Title = "Unauthorized newest activity",
            Description = "Must not appear",
            RequestedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(decoyProperty, decoyUnit, decoyWorkOrder);
        _db.SaveChanges();
        _db.AtomicAuditLogs.Add(Audit(
            nameof(WorkOrder), decoyWorkOrder.Id, AuditLogOperation.Created, now.AddHours(1), 0));

        var assignment = _db.MembershipRoleAssignments
            .Include(item => item.SelectedProperties)
            .Single(item => item.WorkspaceMembership!.AccessContextId == _scope.AccessContextId);
        assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PortfolioId = PortfolioId,
            PropertyId = authorized.Property.Id,
        });
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard!.RecentActivity.Should().NotContain(row => row.EntityId == decoyWorkOrder.Id &&
            row.Type == nameof(WorkOrder));
        dashboard.RecentActivity.Should().Contain(row => row.EntityId == authorized.WorkOrder.Id &&
            row.Type == nameof(WorkOrder));
    }

    [Fact]
    public async Task RecentActivity_FailsClosed_For_Stale_Revision()
    {
        SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(
            _scope with { AccessRevision = _scope.AccessRevision + 1 });

        dashboard!.RecentActivity.Should().BeEmpty();
    }

    [Fact]
    public async Task RecentActivity_FailsClosed_For_Revoked_Session()
    {
        SeedActivityGraph();
        var session = _db.AuthSessions.Single(item => item.Id == _scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        _db.SaveChanges();

        var dashboard = await _sut.GetDashboardAsync(_scope);

        dashboard.Should().BeNull();
    }

    private SeededActivityGraph SeedActivityGraph()
    {
        var baseTime = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        var property = Property("Maple", baseTime);
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1200m,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var tenant1 = Tenant("Maria", "Tenant", baseTime);
        var tenant2 = Tenant("Liam", "Renter", baseTime);
        var tenant3 = Tenant("Noah", "Lessee", baseTime);
        var actor = new ApplicationUser
        {
            UserName = "activity@example.test",
            NormalizedUserName = "ACTIVITY@EXAMPLE.TEST",
            Email = "activity@example.test",
            NormalizedEmail = "ACTIVITY@EXAMPLE.TEST",
            DisplayName = "Activity Actor",
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = "REL-1A",
            EndingDisposition = LeaseManagementEndingDisposition.Undecided,
            CreatedAtUtc = baseTime,
            UpdatedAtUtc = baseTime,
            RowVersion = Guid.NewGuid(),
            CreatedByUser = actor,
        };
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            AccountNumber = "TA-1A",
            Currency = "USD",
            OpenedAtUtc = baseTime,
            CreatedAtUtc = baseTime,
            CreatedByUser = actor,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Fix sink",
            Description = "Leak under the kitchen sink",
            RequestedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Unit,
            Property = property,
            Unit = unit,
            Description = "Plumbing parts",
            Amount = 40m,
            IncurredAt = baseTime,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };

        relationship.Parties.AddRange(
        [
            Party(tenant1, LeaseManagementPartyRole.PrimaryTenant, relationship, actor, baseTime),
            Party(tenant2, LeaseManagementPartyRole.CoTenant, relationship, actor, baseTime),
            Party(tenant3, LeaseManagementPartyRole.Occupant, relationship, actor, baseTime),
        ]);

        _db.AddRange(property, unit, tenant1, tenant2, tenant3, actor, relationship, account, workOrder, expense);
        _db.SaveChanges();

        // Nine property-backed audit rows (<= the Take(10) cap), plus one workspace-global
        // Conversation row. The latter must fail closed because it has no authorized property path.
        _db.AtomicAuditLogs.AddRange(
            Audit("Tenant", tenant1.Id, AuditLogOperation.Created, baseTime, 1),
            Audit("Tenant", tenant2.Id, AuditLogOperation.Updated, baseTime, 2),
            Audit("Tenant", tenant3.Id, AuditLogOperation.Created, baseTime, 3),
            Audit("Unit", unit.Id, AuditLogOperation.Updated, baseTime, 4),
            Audit(nameof(LeaseManagement), relationship.Id, AuditLogOperation.Created, baseTime, 5),
            Audit("WorkOrder", workOrder.Id, AuditLogOperation.Created, baseTime, 6),
            Audit("Property", property.Id, AuditLogOperation.Updated, baseTime, 7),
            Audit(nameof(TenantAccount), account.Id, AuditLogOperation.Updated, baseTime, 8),
            Audit("Expense", expense.Id, AuditLogOperation.Updated, baseTime, 9),
            Audit("Conversation", 999, AuditLogOperation.Created, baseTime, 10));
        _db.SaveChanges();

        return new SeededActivityGraph(property, unit, tenant1, tenant2, tenant3, relationship, account, workOrder, expense);
    }

    private SeededRemainingActivityEntities SeedRemainingActivityEntities(SeededActivityGraph seeded)
    {
        var now = new DateTime(2026, 1, 11, 12, 0, 0, DateTimeKind.Utc);
        var sourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(PortfolioId, 1, now);
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = seeded.Relationship.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-ACTIVITY",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now),
            BaseRentAmount = 1_200m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = sourceVersion,
            CreatedAtUtc = now,
            CreatedByUserId = seeded.Relationship.CreatedByUserId,
            UpdatedAtUtc = now,
        };
        _db.LeaseAgreements.Add(agreement);
        _db.SaveChanges();

        var addendum = new LeaseAddendum
        {
            PublicId = Guid.NewGuid(),
            SeriesPublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = seeded.Relationship.Id,
            BaseAgreementId = agreement.Id,
            VersionNumber = 1,
            AddendumNumber = "ADD-ACTIVITY",
            Purpose = LeaseAddendumPurpose.Other,
            EffectiveFromOn = DateOnly.FromDateTime(now),
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = sourceVersion.Id,
            CreatedAtUtc = now,
            CreatedByUserId = seeded.Relationship.CreatedByUserId,
            UpdatedAtUtc = now,
        };
        var ledgerEntry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = seeded.Account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = 100m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            Description = "Activity ledger entry",
            BusinessKey = "dashboard-activity-ledger",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = seeded.Relationship.CreatedByUserId,
        };
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = seeded.Account.Id,
            OriginatingAgreementId = agreement.Id,
            Currency = "USD",
            CreatedAtUtc = now,
            CreatedByUserId = seeded.Relationship.CreatedByUserId,
        };
        var appointment = new Appointment
        {
            PortfolioId = PortfolioId,
            PropertyId = seeded.Property.Id,
            UnitId = seeded.Unit.Id,
            LeaseManagementId = seeded.Relationship.Id,
            Title = "Activity appointment",
            ScheduledStart = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var inspection = new Inspection
        {
            PortfolioId = PortfolioId,
            PropertyId = seeded.Property.Id,
            UnitId = seeded.Unit.Id,
            LeaseManagementId = seeded.Relationship.Id,
            LeaseAgreementId = agreement.Id,
            ScheduledFor = now.AddDays(2),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = seeded.Property.Id,
            UnitId = seeded.Unit.Id,
            FirstName = "Avery",
            LastName = "Applicant",
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(addendum, ledgerEntry, depositAccount, appointment, inspection, application);
        _db.SaveChanges();

        var depositEntry = new SecurityDepositEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            SecurityDepositAccountId = depositAccount.Id,
            EntryType = SecurityDepositEntryType.Receipt,
            Direction = SecurityDepositDirection.Increase,
            Amount = 100m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            PostedAtUtc = now,
            BusinessKey = "dashboard-activity-deposit",
            Description = "Activity deposit entry",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = seeded.Relationship.CreatedByUserId,
        };
        _db.SecurityDepositEntries.Add(depositEntry);
        _db.SaveChanges();

        _db.AtomicAuditLogs.AddRange(
            Audit(nameof(LeaseAgreement), agreement.Id, AuditLogOperation.Created, now, 0),
            Audit(nameof(LeaseAddendum), addendum.Id, AuditLogOperation.Created, now, 1),
            Audit(nameof(TenantLedgerEntry), checked((int)ledgerEntry.Id), AuditLogOperation.Created, now, 2),
            Audit(nameof(SecurityDepositAccount), depositAccount.Id, AuditLogOperation.Created, now, 3),
            Audit("SecurityDeposit", depositAccount.Id, AuditLogOperation.Updated, now, 4),
            Audit(nameof(SecurityDepositEntry), checked((int)depositEntry.Id), AuditLogOperation.Created, now, 5),
            Audit(nameof(Appointment), appointment.Id, AuditLogOperation.Created, now, 6),
            Audit(nameof(Inspection), inspection.Id, AuditLogOperation.Created, now, 7),
            Audit(nameof(RentalApplication), application.Id, AuditLogOperation.Created, now, 8));
        _db.SaveChanges();

        return new SeededRemainingActivityEntities(
            agreement,
            addendum,
            ledgerEntry,
            depositAccount,
            depositEntry,
            appointment,
            inspection,
            application);
    }

    private static LeaseManagementParty Party(
        Tenant tenant,
        LeaseManagementPartyRole role,
        LeaseManagement relationship,
        ApplicationUser actor,
        DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Tenant = tenant,
        LeaseManagement = relationship,
        Role = role,
        EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
        ChangeReason = "Dashboard activity test",
        CreatedAtUtc = now,
        CreatedByUser = actor,
    };

    private Tenant Tenant(string first, string last, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FirstName = first,
        LastName = last,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Property Property(string name, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Name = name,
        AddressLine1 = "1 Main",
        City = "Columbus",
        State = "OH",
        PostalCode = "43219",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static AtomicAuditLog Audit(string entityType, int entityId, AuditLogOperation op, DateTime baseTime, int minutesAgo)
        => new()
        {
            AttemptId = Guid.NewGuid(),
            CommandType = "test.dashboard-activity.seed",
            CommandIdempotencyKey = Guid.NewGuid().ToString("N"),
            MutationOrdinal = 1,
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = op,
            ActorLabel = "test",
            Timestamp = baseTime.AddMinutes(-minutesAgo),
        };

    private sealed record SeededActivityGraph(
        Property Property,
        Unit Unit,
        Tenant Tenant1,
        Tenant Tenant2,
        Tenant Tenant3,
        LeaseManagement Relationship,
        TenantAccount Account,
        WorkOrder WorkOrder,
        Expense Expense);

    private sealed record SeededRemainingActivityEntities(
        LeaseAgreement Agreement,
        LeaseAddendum Addendum,
        TenantLedgerEntry LedgerEntry,
        SecurityDepositAccount DepositAccount,
        SecurityDepositEntry DepositEntry,
        Appointment Appointment,
        Inspection Inspection,
        RentalApplication Application);
}
