using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

// GetTransactionsAsync is covered separately by AccountingTransactionsViewTests. These service
// tests use the same migrated PostgreSQL schema and API role as production so provider-specific
// functions, views, RLS policies, and constraints remain part of the contract.
[Collection(MigratedPostgreSqlCollection.Name)]
public class AccountingServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private AccountingService _sut = null!;
    private WorkspaceReadScope _scope;
    private int _nextRelationshipSequence = 1;

    public AccountingServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(_commands)]);
        _db = _context.Db;
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(AccountingServiceTests));
        await _context.ActivateApiScopeAsync(_scope);

        _sut = new AccountingService(_db, new ScheduleEService(_db), new YearEndPacketPdfGenerator(), TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsPlainEnglishMoneySnapshot()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        var property = SeedPropertyLeaseAndPayment(now);
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid,
            propertyId: property.Id);

        var summary = await _sut.GetSummaryAsync(_scope, CancellationToken.None);

        summary.Snapshot.Title.Should().Be("Overdue rent needs attention");
        summary.Snapshot.Summary.Should().NotBeNullOrWhiteSpace();
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("overdue"));
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("Repairs"));
    }

    [Fact]
    public async Task GetSummaryAsync_OrdersCategoriesAndReturnsHeadlineRollupInTwoSqlStatements()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        SeedExpense("Minor repair", 40m, now, ScheduleECategory.Repairs, ExpenseStatus.Paid, property.Id);
        SeedExpense("Insurance premium", 125m, now, ScheduleECategory.Insurance, ExpenseStatus.Paid, property.Id);
        SeedExpense("Cleaning", 75m, now, ScheduleECategory.CleaningMaintenance, ExpenseStatus.Paid, property.Id);

        _commands.Clear();

        var summary = await _sut.GetSummaryAsync(_scope, CancellationToken.None);

        summary.ExpensesByCategory.Select(c => c.Category).Should().Equal(
            ScheduleECategory.Insurance,
            ScheduleECategory.CleaningMaintenance,
            ScheduleECategory.Repairs);
        summary.TotalExpenses.Should().Be(240m);

        _commands.Should().HaveCount(2,
            "the page summary uses one category statement and one combined DB-side headline rollup");
        _commands[0].Should().Contain("FROM \"Expenses\"")
            .And.Contain("GROUP BY")
            .And.Contain("ORDER BY",
                "expense category ordering must run in SQL before materialization");
        _commands[1].Should().ContainEquivalentOf("SUM",
            "headline totals must be aggregated by the database rather than from materialized rows");
        _commands[1].Should().Contain("TenantLedgerAllocations",
            "the same statement must derive collected tenant income from canonical allocations");
    }

    [Fact]
    public async Task GetSnapshotAsync_AggregatesMonthToDateWithPlainEnglishExplanations()
    {
        // Anchor everything to "now" so the figures land inside the current month-to-date window
        // regardless of when the test runs.
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var inMonth = monthStart.AddHours(6);

        var (property, lease) = SeedPropertyAndLease(now);

        // Collected this month: $1,200 rent paid.
        SeedPayment(lease, 1200m, dueDate: inMonth, paidInFull: true, paidDate: inMonth);
        // Past due: $1,200 rent overdue (due yesterday, still scheduled) on the same lease.
        SeedPayment(lease, 1200m, dueDate: now.AddDays(-1), paidInFull: false);
        _db.SaveChanges();

        // Spent this month: $456.88 in repairs paid.
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: inMonth,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid,
            propertyId: property.Id);

        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);

        snapshot.Collected.Should().Be(1200m);
        snapshot.Spent.Should().Be(456.88m);
        snapshot.Net.Should().Be(1200m - 456.88m);
        snapshot.PastDueAmount.Should().Be(1200m);
        snapshot.PastDueCount.Should().Be(1);
        snapshot.PeriodStart.Should().Be(monthStart);

        snapshot.Explanations.Collected.Should().Contain("collected").And.Contain("$1,200");
        snapshot.Explanations.Spent.Should().Contain("spent").And.Contain("$456.88");
        snapshot.Explanations.Net.Should().Contain("keeping").And.Contain("$743.12");
        snapshot.Explanations.PastDue.Should().Contain("1 rental").And.Contain("behind");
    }

    [Fact]
    public async Task MoneyReports_ExcludeSecurityDepositsFromCollectedIncome()
    {
        var now = DateTime.UtcNow;
        var (_, lease) = SeedPropertyAndLease(now);

        SeedPayment(lease, 1200m, dueDate: now, paidInFull: true, paidDate: now);
        SeedPayment(lease, 1200m, dueDate: now, paidInFull: true, paidDate: now, entryType: TenantLedgerEntryType.DepositCharge);
        _db.SaveChanges();

        var summary = await _sut.GetSummaryAsync(_scope, CancellationToken.None);
        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);
        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        summary.Payments.Collected.Should().Be(1200m);
        snapshot.Collected.Should().Be(1200m);
        snapshot.Net.Should().Be(1200m);
        reports.TotalIncome.Should().Be(1200m);
        reports.NetCashFlow.Should().Be(1200m);
        reports.Properties.Should().ContainSingle().Which.Income.Should().Be(1200m);
    }

    // Regression for TSK-268: the dashboard "tenants behind" KPI and the "Who's behind" list must
    // never disagree. Both must read from one past-due definition — one row per behind lease — so the
    // KPI count equals the list length and the amounts reconcile, even when a single lease has more
    // than one past-due payment (the previous list counted payments, the KPI counted leases).
    [Fact]
    public async Task GetPastDueAsync_MatchesSnapshotKpi_OneRowPerBehindLease()
    {
        var now = DateTime.UtcNow;

        // Lease A: two past-due payments ($1,000 + $200) → still ONE behind tenant.
        var (_, leaseA) = SeedPropertyAndLease(now);
        SeedPayment(leaseA, 1000m, dueDate: now.AddDays(-10), paidInFull: false);
        SeedPayment(leaseA, 200m, dueDate: now.AddDays(-3), paidInFull: false, entryType: TenantLedgerEntryType.LateFeeCharge);

        // Lease B: one past-due payment ($800) → a second behind tenant.
        var (_, leaseB) = SeedPropertyAndLease(now);
        SeedPayment(leaseB, 800m, dueDate: now.AddDays(-1), paidInFull: false);

        // Lease B also has a paid payment and a future-scheduled one — neither is "behind".
        SeedPayment(leaseB, 800m, dueDate: now.AddDays(-31), paidInFull: true, paidDate: now.AddDays(-30));
        SeedPayment(leaseB, 800m, dueDate: now.AddDays(15), paidInFull: false);
        _db.SaveChanges();

        _commands.Clear();

        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);
        var pastDue = await _sut.GetPastDueAsync(_scope, new PastDueQuery(), CancellationToken.None);

        // The KPI count equals the number of list rows (two distinct behind leases), and the amounts agree.
        snapshot.PastDueCount.Should().Be(2);
        pastDue.TotalCount.Should().Be(2);
        pastDue.Items.Should().HaveCount(2);
        snapshot.PastDueCount.Should().Be(pastDue.TotalCount);

        snapshot.PastDueAmount.Should().Be(2000m); // 1000 + 200 + 800
        pastDue.TotalPastDueAmount.Should().Be(snapshot.PastDueAmount);
        pastDue.BusinessDate.Should().Be(DateOnly.FromDateTime(now));

        // Lease A's row rolls up both of its past-due payments into one tenant.
        var rowA = pastDue.Items.Single(i => i.LeaseManagementId == leaseA.Id);
        rowA.OverduePaymentCount.Should().Be(2);
        rowA.PastDueAmount.Should().Be(1200m);

        // Ordered by who's waited longest (oldest due date first) → Lease A leads.
        pastDue.Items.First().LeaseManagementId.Should().Be(leaseA.Id);

        var sql = string.Join("\n---\n", _commands);
        sql.Should().ContainEquivalentOf("COUNT", "past-due tenant counts must be aggregated in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("past-due balances must be summed in SQL");
        sql.Should().Contain("GROUP BY", "the SQL aggregate should be over the per-lease past-due grouping");
    }

    [Fact]
    public async Task GetPastDueAsync_ProjectsMetadataAndOldestPaymentInSingleRowQuery()
    {
        var now = DateTime.UtcNow;
        var (_, lease) = SeedPropertyAndLease(now);
        lease.LeaseManagement!.Parties.Single().Tenant!.Phone = "614-555-0130";

        var oldest = SeedPayment(lease, 900m, dueDate: now.AddDays(-20), paidInFull: false);
        var partial = SeedPayment(lease, 500m, dueDate: now.AddDays(-5), paidInFull: false, amountPaid: 125m);
        _db.SaveChanges();
        _commands.Clear();

        var pastDue = await _sut.GetPastDueAsync(_scope, new PastDueQuery(), CancellationToken.None);

        var row = pastDue.Items.Should().ContainSingle().Subject;
        row.LeaseManagementId.Should().Be(lease.LeaseManagementId);
        row.TenantName.Should().Be("Maria Tenant");
        row.TenantPhone.Should().Be("614-555-0130");
        row.RelationshipNumber.Should().Be("L-001");
        row.PropertyName.Should().Be("General");
        row.UnitNumber.Should().Be("12");
        row.UnitId.Should().Be(lease.LeaseManagement.UnitId, "the row carries the relationship's unit so the oldest charge link folds into the unit's Rent tab");
        row.OldestLedgerEntryId.Should().Be(oldest.Id);
        row.OldestDueOn.Should().Be(oldest.DueOn);
        row.PastDueAmount.Should().Be(1275m);
        row.OverduePaymentCount.Should().Be(2);

        var selects = _commands
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        selects.Should().HaveCount(2, "past-due drill-down should run one summary query and one row projection query");
        selects[1].Should().Contain("\"LeaseManagements\"", "row metadata should come from the canonical household relationship");
        selects[1].Should().Contain(
            "vw_lease_management_lifecycle",
            "current agreement and primary-tenant facts should come from the canonical lifecycle projection");
        selects[1].Should().Contain("\"Tenants\"", "tenant labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("\"Properties\"", "property labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("\"Units\"", "unit labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("ORDER BY", "oldest-payment selection and row ordering should be SQL-side");
        selects[1].Should().Contain("LIMIT", "the row page must be bounded in SQL before materialization");
    }

    [Fact]
    public async Task GetPastDueAsync_PagesAfterCanonicalSqlOrdering_WhileKeepingExactTotals()
    {
        var now = DateTime.UtcNow;
        var (_, oldestLease) = SeedPropertyAndLease(now);
        SeedPayment(oldestLease, 700m, dueDate: now.AddDays(-20), paidInFull: false);
        var (_, middleLease) = SeedPropertyAndLease(now);
        SeedPayment(middleLease, 800m, dueDate: now.AddDays(-10), paidInFull: false);
        var (_, newestLease) = SeedPropertyAndLease(now);
        SeedPayment(newestLease, 900m, dueDate: now.AddDays(-5), paidInFull: false);
        await _db.SaveChangesAsync();
        _commands.Clear();

        var page = await _sut.GetPastDueAsync(
            _scope,
            new PastDueQuery { Skip = 1, Take = 1 },
            CancellationToken.None);

        page.TotalCount.Should().Be(3);
        page.TotalPastDueAmount.Should().Be(2400m);
        page.BusinessDate.Should().Be(DateOnly.FromDateTime(now));
        page.Skip.Should().Be(1);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle()
            .Which.LeaseManagementId.Should().Be(middleLease.LeaseManagementId);

        var selects = _commands
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        selects.Should().HaveCount(2, "totals and the bounded page are each one SQL statement");
        selects[1].Should().Contain("ORDER BY");
        selects[1].Should().Contain("LIMIT");
        selects[1].Should().Contain("OFFSET");
    }

    [Fact]
    public async Task GetPastDueAsync_KeepsOpenPossessionActionableAfterFixedTermAgreementExpires()
    {
        var now = DateTime.UtcNow;

        var (_, endedLease) = SeedPropertyAndLease(
            now,
            agreementNumber: "L-ENDED",
            termStartOn: DateOnly.FromDateTime(now.AddYears(-2)),
            termEndOn: DateOnly.FromDateTime(now.AddMonths(-1)));
        SeedPayment(endedLease, 925m, dueDate: now.AddMonths(-6), paidInFull: false);

        var (_, currentLease) = SeedPropertyAndLease(
            now,
            agreementNumber: "L-CURRENT");
        SeedPayment(currentLease, 975m, dueDate: now.AddDays(-5), paidInFull: false);
        await _db.SaveChangesAsync();

        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);
        var pastDue = await _sut.GetPastDueAsync(_scope, new PastDueQuery(), CancellationToken.None);

        snapshot.PastDueCount.Should().Be(2);
        snapshot.PastDueAmount.Should().Be(1900m);
        pastDue.TotalCount.Should().Be(2);
        pastDue.TotalPastDueAmount.Should().Be(1900m);
        pastDue.Items.Should().ContainSingle(i => i.LeaseManagementId == currentLease.LeaseManagementId);
        var holdover = pastDue.Items.Should()
            .ContainSingle(i => i.LeaseManagementId == endedLease.LeaseManagementId).Subject;
        holdover.CurrentAgreementId.Should().BeNull(
            "the agreement is expired even though the possession-backed resident relationship remains occupied");
    }

    [Fact]
    public async Task GetPastDueAsync_ExcludesReturnedPossessionFromCurrentMoneyAttention()
    {
        var now = DateTime.UtcNow;

        var (_, returnedLease) = SeedPropertyAndLease(now);
        returnedLease.LeaseManagement!.PossessionReturnedAtUtc = now.AddDays(-1);
        SeedPayment(returnedLease, 925m, dueDate: now.AddMonths(-1), paidInFull: false);

        var (_, currentLease) = SeedPropertyAndLease(now);
        SeedPayment(currentLease, 975m, dueDate: now.AddDays(-5), paidInFull: false);
        await _db.SaveChangesAsync();

        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);
        var pastDue = await _sut.GetPastDueAsync(_scope, new PastDueQuery(), CancellationToken.None);

        snapshot.PastDueCount.Should().Be(1);
        snapshot.PastDueAmount.Should().Be(975m);
        pastDue.TotalCount.Should().Be(1);
        pastDue.TotalPastDueAmount.Should().Be(975m);
        pastDue.Items.Should().ContainSingle(i => i.LeaseManagementId == currentLease.LeaseManagementId);
        pastDue.Items.Should().NotContain(i => i.LeaseManagementId == returnedLease.LeaseManagementId);
    }

    [Fact]
    public async Task GetReportsAsync_LedgerEntriesCarryPlainEnglishExplanations()
    {
        var now = new DateTime(2026, 03, 03, 12, 0, 0, DateTimeKind.Utc);
        var (_, lease) = SeedPropertyAndLease(now);

        var receipt = SeedPayment(lease, 1200m,
            dueDate: new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            paidInFull: true, paidDate: now);
        _db.SaveChanges();

        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        var receiptEntry = reports.Ledger.Single(l => l.Type == "TenantLedger");
        receiptEntry.Explanation.Should().Be("Tenant receipt posted on Mar 3, 2026.");
        receiptEntry.TenantAccountId.Should().Be(receipt.TenantAccountId);
        receiptEntry.SourceHref.Should().Be(
            $"/tenant-accounts/{receipt.TenantAccountId}/entries/{receipt.Id}");
    }

    [Fact]
    public async Task GetReportsAsync_ScheduleEUsesTaxRollupIncludingModeledInterestAndDepreciation()
    {
        var year = DateTime.UtcNow.Year;
        var now = new DateTime(year, 03, 03, 12, 0, 0, DateTimeKind.Utc);
        var (property, lease) = SeedPropertyAndLease(now);
        property.PurchasePrice = 300_000m;
        property.LandValue = 60_000m;
        property.InServiceDate = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc);

        SeedPayment(lease, 1_200m, dueDate: now, paidInFull: true, paidDate: now);
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Category = ScheduleECategory.Repairs,
            Description = "Sink repair",
            Status = ExpenseStatus.Paid,
            Amount = 1_000m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        var roof = new CapitalAsset
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Description = "Roof replacement",
            CostBasis = 9_900m,
            InServiceDate = new DateTime(year, 08, 01, 0, 0, 0, DateTimeKind.Utc),
            Method = DepreciationMethod.StraightLine,
            RecoveryYears = RecoveryClass.ResidentialBuilding,
            Convention = DepreciationConvention.MidMonth,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.CapitalAssets.Add(roof);
        _db.SaveChanges();
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            PropertyId = property.Id,
            CapitalizedAssetId = roof.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Capitalized roof invoice",
            Status = ExpenseStatus.Paid,
            Amount = 9_900m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Lender = "Bank",
            OriginalAmount = 100_000m,
            CurrentBalance = 100_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = new DateTime(year, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 600m,
            Status = LoanStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Loans.Add(loan);
        _db.SaveChanges();
        _db.LoanPayments.AddRange(
            new LoanPayment
            {
                PortfolioId = PortfolioId,
                LoanId = loan.Id,
                PeriodKey = $"{year}-01",
                DueDate = new DateTime(year, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 600m,
                PrincipalAmount = 100m,
                EscrowAmount = 0m,
                TotalAmount = 700m,
                BalanceAfter = 99_900m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = now,
            },
            new LoanPayment
            {
                PortfolioId = PortfolioId,
                LoanId = loan.Id,
                PeriodKey = $"{year}-02",
                DueDate = new DateTime(year, 02, 01, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 590m,
                PrincipalAmount = 110m,
                EscrowAmount = 0m,
                TotalAmount = 700m,
                BalanceAfter = 99_790m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = now,
            });
        _db.SaveChanges();

        _commands.Clear();

        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.Repairs &&
            c.Total == 1_000m);
        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.MortgageInterest &&
            c.Total == 1_190m);
        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.Depreciation &&
            c.Total == 8_862.27m);
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "regular Schedule E categories must be grouped and summed in SQL");
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"LoanPayments\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "modeled mortgage interest must be grouped and summed in SQL");

        var scheduleERollupSql = _commands.Where(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LoanPayments", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("CapitalAssets", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("UNION ALL", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)).ToList();
        scheduleERollupSql.Should().ContainSingle(
            "expense, modeled-interest, and shared depreciation components must be combined, grouped, and ordered by one SQL statement");
        (scheduleERollupSql[0].Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
         scheduleERollupSql[0].Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("Schedule E category totals and counts must be aggregated in SQL");
    }

    [Fact]
    public async Task GetReportsAsync_BuildsAuthorizedLedgerWithSqlUnionAndOrdering()
    {
        var now = new DateTime(2026, 03, 03, 12, 0, 0, 0, DateTimeKind.Utc);
        var (property, lease) = SeedPropertyAndLease(now);

        SeedPayment(lease, 1200m, dueDate: now.AddDays(-2), paidInFull: true, paidDate: now);

        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Ace Plumbing",
            ServiceType = "Plumbing",
            Is1099Eligible = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Vendors.Add(vendor);
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Vendor = vendor,
            Category = ScheduleECategory.Repairs,
            Description = "Sink repair",
            Status = ExpenseStatus.Paid,
            Amount = 225m,
            IncurredAt = now.AddDays(-1),
            PaidAt = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        _commands.Clear();

        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        reports.Ledger.Should().Contain(l => l.Type == "TenantLedger" && l.Amount == 1200m);
        reports.Ledger.Should().Contain(l => l.Type == "Expense" && l.Amount == -225m);
        reports.Ledger.Should().NotContain(l => l.Type == "Bank",
            "unassigned banking rows require separate banking authority and are not accounting-report rows");

        var ledgerSql = _commands.FirstOrDefault(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        ledgerSql.Should().NotBeNull("the report ledger must filter, combine, and sort rows as one DB-side query");
        ledgerSql!.Should().Contain("ORDER BY", "ledger sorting must run in SQL");
        ledgerSql.Should().NotContain("\"BankTransactions\"",
            "the canonical accounting read must not cross into unassigned banking data");
    }

    [Fact]
    public async Task GetReportsAsync_ReturnsRecentLedgerPreviewAndTotalCount()
    {
        var now = new DateTime(2026, 07, 01, 12, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        for (var i = 0; i < 10; i++)
        {
            SeedExpense(
                description: $"Expense {i:D2}",
                amount: 100m + i,
                incurredAt: now.AddDays(i),
                category: ScheduleECategory.Repairs,
                status: ExpenseStatus.Paid,
                propertyId: property.Id);
        }

        _commands.Clear();

        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        reports.LedgerTotalCount.Should().Be(10);
        reports.RecentLedger.Should().HaveCount(8);
        reports.Ledger.Should().HaveCount(8);
        reports.RecentLedger[0].Description.Should().Be("Expense 09");
        reports.RecentLedger[^1].Description.Should().Be("Expense 02");
        var propertyReport = reports.Properties.Should()
            .ContainSingle(report => report.PropertyId == property.Id).Subject;
        propertyReport.Income.Should().Be(0m);
        propertyReport.Expenses.Should().Be(1045m);
        propertyReport.Overdue.Should().Be(0m);
        propertyReport.OverdueCount.Should().Be(0);
        _commands.Should().Contain(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
            "the report ledger preview must be limited by the database, not sliced in the web page");
    }

    [Fact]
    public async Task GetYearEndPacketDataAsync_UsesExpenseAllocationsExactlyOnce()
    {
        var now = new DateTime(2026, 01, 15, 12, 0, 0, DateTimeKind.Utc);
        var (maple, _) = SeedPropertyAndLease(now);
        var oak = SeedProperty("Oak", now);
        var expense = SeedExpense(
            description: "Shared insurance",
            amount: 300m,
            incurredAt: now,
            category: ScheduleECategory.Insurance,
            status: ExpenseStatus.Paid,
            propertyId: null);
        expense.PaidAt = now;
        _db.ExpenseAllocations.AddRange(
            new ExpenseAllocation
            {
                PortfolioId = PortfolioId,
                ExpenseId = expense.Id,
                TargetKind = ExpenseAllocationTargetKind.Property,
                PropertyId = maple.Id,
                Amount = 125m,
                CreatedAt = now,
            },
            new ExpenseAllocation
            {
                PortfolioId = PortfolioId,
                ExpenseId = expense.Id,
                TargetKind = ExpenseAllocationTargetKind.Property,
                PropertyId = oak.Id,
                Amount = 175m,
                CreatedAt = now,
            });
        _db.SaveChanges();

        _commands.Clear();

        var packet = await _sut.GetYearEndPacketDataAsync(_scope, 2026, CancellationToken.None);

        packet.Properties.Single(property => property.PropertyId == maple.Id).TotalExpenses.Should().Be(125m);
        packet.Properties.Single(property => property.PropertyId == oak.Id).TotalExpenses.Should().Be(175m);
        packet.CashFlowMoneyOut.Should().Be(300m);
        packet.CashFlow.Single(month => month.Month == 1).MoneyOut.Should().Be(300m);

        var sql = string.Join("\n---\n", _commands);
        sql.Should().Contain("\"ExpenseAllocations\"",
            "year-end packet expenses must use the financial report allocation projection");
        var hasServerSideSum =
            sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase);
        hasServerSideSum.Should().BeTrue("allocation amounts must be aggregated in SQL");
    }

    [Fact]
    public async Task GetYearEndPacketDataAsync_UnassignedPortfolioExpensesRequireAllPropertiesReportsReadAuthority()
    {
        var now = new DateTime(2026, 01, 15, 12, 0, 0, DateTimeKind.Utc);
        var (maple, _) = SeedPropertyAndLease(now);
        SeedExpense(
            description: "Maple repair",
            amount: 100m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid,
            propertyId: maple.Id);
        SeedExpense(
            description: "Portfolio bookkeeping",
            amount: 75m,
            incurredAt: now,
            category: ScheduleECategory.Other,
            status: ExpenseStatus.Paid,
            propertyId: null);
        var selectedScope = _db.SeedPropertyManagerScope(
            PortfolioId,
            maple.Id,
            "year-end-selected-portfolio-expense");

        _commands.Clear();

        var allProperties = await _sut.GetYearEndPacketDataAsync(_scope, 2026, CancellationToken.None);

        allProperties.CashFlowMoneyOut.Should().Be(175m);
        allProperties.CashFlow.Single(month => month.Month == 1).MoneyOut.Should().Be(175m);
        allProperties.Properties.Single(property => property.PropertyId == maple.Id)
            .TotalExpenses.Should().Be(100m, "unassigned portfolio expenses do not appear on a property row");
        _commands.Should().Contain(sql =>
            sql.Contains("rc_api_effective_capability_scopes", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"ScopeKind\"", StringComparison.Ordinal),
            "unassigned year-end packet expenses require effective AllProperties reports.read authority in SQL");

        _commands.Clear();

        await _context.ActivateApiScopeAsync(selectedScope);
        var selectedProperties = await _sut.GetYearEndPacketDataAsync(selectedScope, 2026, CancellationToken.None);

        selectedProperties.CashFlowMoneyOut.Should().Be(100m);
        selectedProperties.CashFlow.Single(month => month.Month == 1).MoneyOut.Should().Be(100m);
        selectedProperties.Properties.Should().ContainSingle();
        selectedProperties.Properties[0].TotalExpenses.Should().Be(100m);
    }

    [Fact]
    public async Task GetSummaryAndReports_ExcludeUnassignedBankActivity()
    {
        SeedPropertyLeaseAndPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        SeedBankTransaction(
            description: "Tenant ACH",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            category: "Deposit",
            matchStatus: "Unmatched");
        SeedBankTransaction(
            description: "Hardware supply",
            merchantName: "Hardware Store",
            amount: -84.25m,
            postedAt: new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc),
            category: "Withdrawal",
            matchStatus: "Unmatched");
        SeedBankTransaction(
            description: "Matched duplicate deposit",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc),
            category: "Deposit",
            matchStatus: "Matched",
            matchedPaymentId: 1);

        var summary = await _sut.GetSummaryAsync(_scope, CancellationToken.None);
        var reports = await _sut.GetReportsAsync(_scope, CancellationToken.None);

        summary.Payments.Collected.Should().Be(0m);
        summary.TotalExpenses.Should().Be(0m);
        reports.TotalIncome.Should().Be(0m);
        reports.TotalExpenses.Should().Be(0m);
        reports.Ledger.Should().NotContain(l => l.Type == "Bank");
        reports.Ledger.Should().NotContain(l => l.Description == "Matched duplicate deposit");
    }

    [Fact]
    public async Task GetSummaryAndSnapshot_ExcludeBankRowsWithoutPropertyScopedAccountingAuthority()
    {
        // Anchor to "now" so the snapshot's month-to-date window includes the seeded rows regardless
        // of when the test runs. SeedPropertyLeaseAndPayment seeds a Scheduled (not Paid) payment, so
        // recorded payments contribute $0 to Collected here.
        var now = DateTime.UtcNow;
        SeedPropertyLeaseAndPayment(now);

        // Confirmed match (Matched + a payment link): the bank deposit is the SAME money as the
        // recorded payment, so it must NOT be added on top — no double count.
        SeedBankTransaction(
            description: "Confirmed rent deposit",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: now,
            category: "Deposit",
            matchStatus: "Matched",
            matchedPaymentId: 1);
        // Dismissed deposit: reviewed, decided NOT a match, no link. It is real money that should
        // still be counted as collected.
        SeedBankTransaction(
            description: "Dismissed deposit",
            merchantName: "Other",
            amount: 300m,
            postedAt: now,
            category: "Deposit",
            matchStatus: "Dismissed");

        var summary = await _sut.GetSummaryAsync(_scope, CancellationToken.None);
        var snapshot = await _sut.GetSnapshotAsync(_scope, CancellationToken.None);

        summary.Payments.Collected.Should().Be(0m);
        snapshot.Collected.Should().Be(0m);
    }

    private (Property Property, LeaseAgreement Agreement) SeedPropertyAndLease(
        DateTime now,
        string? agreementNumber = null,
        DateOnly? termStartOn = null,
        DateOnly? termEndOn = null)
    {
        var relationshipNumber = $"L-{_nextRelationshipSequence++:D3}";
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "General",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "12",
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, unit, tenant);
        _db.SaveChanges();
        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = relationshipNumber,
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();
        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}",
            Currency = "USD",
            OpenedAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Accounting test",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = agreementNumber ?? relationshipNumber,
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = termStartOn ?? DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = termEndOn ?? DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = termStartOn ?? DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 1200m,
            RentDueDay = 1,
            SecurityDepositObligation = 1200m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            LeaseManagement = management,
        };
        management.TenantAccount = account;
        management.Parties.Add(party);
        _db.AddRange(account, party, agreement);
        _db.SaveChanges();
        _db.MarkFullyExecuted(
            agreement,
            party,
            tenant,
            actorUserId: 1,
            executedAtUtc: now);
        return (property, agreement);
    }

    private Property SeedPropertyLeaseAndPayment(DateTime now)
    {
        var (property, agreement) = SeedPropertyAndLease(now);
        SeedPayment(agreement, 1200m, dueDate: now.AddDays(-1), paidInFull: false);
        return property;
    }

    private Property SeedProperty(string name, DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{name} Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private TenantLedgerEntry SeedPayment(
        LeaseAgreement agreement, decimal amount, DateTime dueDate, bool paidInFull,
        DateTime? paidDate = null,
        TenantLedgerEntryType entryType = TenantLedgerEntryType.RentCharge,
        decimal? amountPaid = null)
    {
        var account = agreement.LeaseManagement!.TenantAccount!;
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = entryType,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueDate),
            DueOn = DateOnly.FromDateTime(dueDate),
            PostedAtUtc = dueDate,
            Description = entryType.ToString(),
            BusinessKey = $"charge:{Guid.NewGuid():N}",
            LeaseAgreementId = agreement.Id,
            CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(charge);
        _db.SaveChanges();
        var paid = paidInFull ? amount : amountPaid ?? 0m;
        if (paid <= 0m) return charge;
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = paid,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidDate ?? dueDate),
            PostedAtUtc = paidDate ?? dueDate,
            Description = "Payment received by check",
            BusinessKey = $"receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        _db.TenantLedgerEntries.Add(receipt);
        _db.SaveChanges();
        _db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = paid,
            AllocatedAtUtc = paidDate ?? dueDate,
            BusinessKey = $"allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        _db.SaveChanges();
        return receipt;
    }

    private Expense SeedExpense(
        string description,
        decimal amount,
        DateTime incurredAt,
        ScheduleECategory category,
        ExpenseStatus status,
        int? propertyId = null,
        bool save = true)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = propertyId.HasValue
                ? ExpenseOperationalScope.Property
                : ExpenseOperationalScope.Portfolio,
            PropertyId = propertyId,
            Category = category,
            Description = description,
            Status = status,
            Amount = amount,
            IncurredAt = incurredAt,
            CreatedAt = incurredAt,
            UpdatedAt = incurredAt,
        };
        _db.Expenses.Add(expense);
        if (save) _db.SaveChanges();
        return expense;
    }

    private BankTransaction SeedBankTransaction(
        string description,
        string merchantName,
        decimal amount,
        DateTime postedAt,
        string category,
        string matchStatus,
        long? matchedPaymentId = null,
        int? matchedExpenseId = null)
    {
        var connection = _db.BankConnections.FirstOrDefault() ?? new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Plaid",
            InstitutionName = "Sandbox Bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        if (connection.Id == 0) _db.BankConnections.Add(connection);

        var transaction = new BankTransaction
        {
            PortfolioId = PortfolioId,
            BankConnection = connection,
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            PostedAt = postedAt,
            Description = description,
            MerchantName = merchantName,
            Amount = amount,
            IsoCurrencyCode = "USD",
            Category = category,
            MatchStatus = matchStatus,
            MatchedTenantLedgerEntryId = matchedPaymentId,
            MatchedExpenseId = matchedExpenseId,
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        _db.BankTransactions.Add(transaction);
        _db.SaveChanges();
        return transaction;
    }
}
internal sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        commands.Add(command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        commands.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
