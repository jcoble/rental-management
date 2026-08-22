using System.Data.Common;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Services;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.Api.Tests.Domain;

// These service tests use the same migrated PostgreSQL schema and API role as production so
// provider-specific functions, views, RLS policies, and constraints remain part of the contract.
[Collection(MigratedPostgreSqlCollection.Name)]
public class AccountingServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private AccountingService _sut = null!;
    private WorkspaceReadScope _scope;
    private int _nextRelationshipSequence = 1;

    public AccountingServiceTests(MigratedPostgreSqlFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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
        row.OldestLedgerEntryOpenAmount.Should().Be(900m,
            "the receipt target amount must belong to the exact oldest charge, not the account aggregate");
        row.PastDueAmount.Should().Be(1275m);
        row.TotalOpenBalance.Should().Be(1275m,
            "the dialog amount cap must come from the server-owned tenant account balance");
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
        selects[1].Should().Contain("\"OpenAmount\"",
            "the exact target charge open amount must be projected by the same SQL row query");
        selects[1].Should().Contain("ORDER BY", "oldest-payment selection and row ordering should be SQL-side");
        selects[1].Should().Contain("LIMIT", "the row page must be bounded in SQL before materialization");
        selects.Should().OnlyContain(sql =>
            sql.Contains("authorized_properties AS MATERIALIZED", StringComparison.Ordinal),
            "past-due summary and page queries must join one materialized authorization set");
    }

    [Fact]
    public async Task GetPastDueAndSnapshot_ExcludeLeaseOutsideSelectedPropertyScope()
    {
        var now = DateTime.UtcNow;
        var (selectedProperty, selectedLease) = SeedPropertyAndLease(now);
        var (excludedProperty, excludedLease) = SeedPropertyAndLease(now);
        SeedPayment(selectedLease, 700m, dueDate: now.AddDays(-2), paidInFull: false);
        SeedPayment(excludedLease, 900m, dueDate: now.AddDays(-2), paidInFull: false);
        _db.SaveChanges();

        var selectedScope = _db.SeedPropertyManagerScope(
            PortfolioId,
            selectedProperty.Id,
            nameof(GetPastDueAndSnapshot_ExcludeLeaseOutsideSelectedPropertyScope));
        await _context.ActivateApiScopeAsync(selectedScope);
        _commands.Clear();

        var pastDue = await _sut.GetPastDueAsync(selectedScope, new PastDueQuery(), CancellationToken.None);
        var snapshot = await _sut.GetSnapshotAsync(selectedScope, CancellationToken.None);

        pastDue.Items.Should().ContainSingle(item => item.LeaseManagementId == selectedLease.LeaseManagementId);
        pastDue.Items.Should().NotContain(item => item.LeaseManagementId == excludedLease.LeaseManagementId);
        pastDue.TotalPastDueAmount.Should().Be(700m);
        snapshot.PastDueCount.Should().Be(1);
        snapshot.PastDueAmount.Should().Be(700m);
        _commands
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql => sql.Contains("authorized_properties AS MATERIALIZED", StringComparison.Ordinal),
                "past-due and snapshot SQL must preserve the selected-property authorization boundary");
        _commands
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql => sql.Contains("public.rc_api_effective_capability_scopes(", StringComparison.Ordinal),
                "each accounting statement must resolve authorization through the shared scope relation");
        excludedProperty.Id.Should().NotBe(selectedProperty.Id);
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

    [Theory]
    [InlineData("Rent", TenantLedgerEntryType.RentCharge)]
    [InlineData("RentCharge", TenantLedgerEntryType.RentCharge)]
    [InlineData("LateFee", TenantLedgerEntryType.LateFeeCharge)]
    [InlineData("LateFeeCharge", TenantLedgerEntryType.LateFeeCharge)]
    public async Task GetTransactionsAsync_TenantCategoryIncludesChargeAndAllocatedReceipt(
        string category,
        TenantLedgerEntryType entryType)
    {
        var now = new DateTime(2027, 2, 15, 12, 0, 0, DateTimeKind.Utc);
        var (_, lease) = SeedPropertyAndLease(now);
        SeedPayment(
            lease,
            entryType == TenantLedgerEntryType.RentCharge ? 1_050m : 52.50m,
            dueDate: now,
            paidInFull: true,
            paidDate: now,
            entryType: entryType);
        _commands.Clear();

        var result = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery
            {
                Category = category,
                Take = 20,
            },
            CancellationToken.None);

        result.TotalCount.Should().Be(2);
        result.Items.Should().ContainSingle(row => row.Category == entryType.ToString());
        result.Items.Should().ContainSingle(row => row.Category == nameof(TenantLedgerEntryType.PaymentReceipt));
        _commands.Should().HaveCount(3,
            "the grid uses a slim count, ordered page keys, and one page-keyed hydration query");
        _commands.Take(2).Should().OnlyContain(sql =>
            sql.Contains("\"TenantLedgerAllocations\"", StringComparison.OrdinalIgnoreCase),
            "both the count and page query must resolve receipt categories from allocations DB-side");
        _commands[2].Should()
            .Contain("page_ledger_entries AS MATERIALIZED")
            .And.Contain("entry.\"Id\" = ANY(@ledgerIds::bigint[])")
            .And.NotContain("\"StoredFiles\"")
            .And.NotContain("\"BankTransactions\"",
                "a ledger-only page hydrates only its branch and must not enter expense fact tables");
    }

    [Fact]
    public async Task GetTransactionsAsync_OffersOnlyStatusPairsBackedByTheTransactionProjection()
    {
        var now = new DateTime(2027, 2, 15, 12, 0, 0, DateTimeKind.Utc);
        var (property, lease) = SeedPropertyAndLease(now);
        var payment = SeedPayment(lease, 1_050m, dueDate: now, paidInFull: true, paidDate: now);
        var pending = SeedExpense("Pending expense", 10m, now, ScheduleECategory.Repairs, ExpenseStatus.Pending, property.Id);
        var approved = SeedExpense("Approved expense", 20m, now, ScheduleECategory.Repairs, ExpenseStatus.Approved, property.Id);
        var paid = SeedExpense("Paid expense", 30m, now, ScheduleECategory.Repairs, ExpenseStatus.Paid, property.Id);

        var offeredPairs = new[]
        {
            (Kind: "Payment", Status: "Credit", Id: (long)payment.Id),
            (Kind: "Expense", Status: "Pending", Id: (long)pending.Id),
            (Kind: "Expense", Status: "Approved", Id: (long)approved.Id),
            (Kind: "Expense", Status: "Paid", Id: (long)paid.Id),
        };

        offeredPairs.Should().HaveCount(4,
            "the rendered filter offers one Payment status and three Expense statuses");

        foreach (var pair in offeredPairs)
        {
            var result = await _sut.GetTransactionsAsync(
                _scope,
                new AccountingTransactionsQuery { Kind = pair.Kind, Status = pair.Status, Take = 20 },
                CancellationToken.None);

            result.TotalCount.Should().Be(1, $"{pair.Kind}/{pair.Status} must match exactly one seeded projected row");
            result.Items.Should().ContainSingle(row => row.Kind == pair.Kind && row.Id == pair.Id,
                $"{pair.Kind}/{pair.Status} must match a seeded projected row");
        }

        var unsupportedPairs = new[]
        {
            (Kind: "Payment", Status: "Pending"),
            (Kind: "Payment", Status: "Approved"),
            (Kind: "Payment", Status: "Paid"),
            (Kind: "Expense", Status: "Credit"),
        };
        foreach (var pair in unsupportedPairs)
        {
            var unsupported = await _sut.GetTransactionsAsync(
                _scope,
                new AccountingTransactionsQuery { Kind = pair.Kind, Status = pair.Status, Take = 20 },
                CancellationToken.None);
            unsupported.Items.Should().BeEmpty(
                $"{pair.Kind}/{pair.Status} is not an offered pair and must be normalized before the client request");
        }
    }

    [Fact]
    public async Task GetTransactionsAsync_EnrichesOnlyPagedExpensesWithReceiptAndBankFacts()
    {
        var now = new DateTime(2027, 2, 16, 12, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        var enrichedExpense = SeedExpense(
            "Receipt and bank expense",
            225m,
            now.AddDays(-1),
            ScheduleECategory.Repairs,
            ExpenseStatus.Paid,
            property.Id);
        var plainExpense = SeedExpense(
            "Expense without supporting facts",
            75m,
            now.AddDays(-2),
            ScheduleECategory.CleaningMaintenance,
            ExpenseStatus.Pending,
            property.Id);
        _db.StoredFiles.AddRange(
            new StoredFile
            {
                PortfolioId = PortfolioId,
                FileName = "receipt.pdf",
                FilePath = $"accounting/{enrichedExpense.Id}/receipt.pdf",
                ContentType = "application/pdf",
                FileSize = 128,
                EntityType = "Expense",
                EntityId = enrichedExpense.Id,
                UploadedAt = now.AddHours(-2),
            },
            new StoredFile
            {
                PortfolioId = PortfolioId,
                FileName = "receipt.jpg",
                FilePath = $"accounting/{enrichedExpense.Id}/receipt.jpg",
                ContentType = "image/jpeg",
                FileSize = 256,
                EntityType = "Expense",
                EntityId = enrichedExpense.Id,
                UploadedAt = now.AddHours(-1),
            });
        _db.SaveChanges();

        var clearedAt = now.AddMinutes(-30);
        SeedBankTransaction(
            "Cleared repair",
            "Repair vendor",
            -225m,
            clearedAt,
            "Withdrawal",
            "Matched",
            matchedExpenseId: enrichedExpense.Id);
        _commands.Clear();

        var result = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery
            {
                Kind = "Expense",
                Take = 20,
            },
            CancellationToken.None);
        foreach (var (sql, index) in _commands.Select((sql, index) => (sql, index)))
        {
            _output.WriteLine($"--- ACCOUNTING TRANSACTIONS STATEMENT {index + 1} ---");
            _output.WriteLine(sql);
        }

        result.TotalCount.Should().Be(2);
        var enriched = result.Items.Single(row => row.Id == enrichedExpense.Id);
        enriched.HasReceipt.Should().BeTrue();
        enriched.ReceiptIsImage.Should().BeTrue("the newest active receipt is an image");
        enriched.Reconciled.Should().BeTrue();
        enriched.ClearedBankName.Should().Be("Sandbox Bank");
        enriched.ClearedAt.Should().Be(clearedAt);

        var plain = result.Items.Single(row => row.Id == plainExpense.Id);
        plain.HasReceipt.Should().BeFalse();
        plain.ReceiptIsImage.Should().BeFalse();
        plain.Reconciled.Should().BeFalse();
        plain.ClearedBankName.Should().BeNull();
        plain.ClearedAt.Should().BeNull();

        _commands.Should().HaveCount(3,
            "the grid uses count + ordered page keys + one page-keyed hydration statement");
        var pageSql = _commands[1];
        pageSql.Should().Contain("UNION ALL").And.Contain("ORDER BY").And.Contain("LIMIT");
        pageSql.Should().NotContain("\"StoredFiles\"")
            .And.NotContain("\"BankTransactions\"",
                "receipt and bank tables must not be re-entered by each output-row projection");

        var hydrationSql = _commands.Single(sql => sql.Contains("\"StoredFiles\""));
        hydrationSql.Should()
            .Contain("page_expenses AS MATERIALIZED")
            .And.Contain("expense.\"Id\" = ANY(@expenseIds::integer[])")
            .And.Contain("file.\"EntityId\" = ANY(@expenseIds::bigint[])")
            .And.Contain("bank.\"MatchedExpenseId\" = ANY(@expenseIds::integer[])")
            .And.Contain("GROUP BY")
            .And.Contain("array_agg")
            .And.Contain("UploadedAt",
                "receipt presence and newest-receipt type must be computed DB-side for page expense ids");
        hydrationSql.Should().Contain("\"BankTransactions\"")
            .And.Contain("\"BankConnections\"",
                "the newest matched bank fact is folded into the same page-keyed hydration statement");
    }

    [Fact]
    public async Task GetTransactionsAsync_SelectsHigherIdForEqualReceiptAndBankTimestamps()
    {
        var now = new DateTime(2027, 2, 17, 12, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        var expense = SeedExpense(
            "Equal timestamp tie expense",
            225m,
            now,
            ScheduleECategory.Repairs,
            ExpenseStatus.Paid,
            property.Id);

        var uploadedAt = now.AddHours(-1);
        _db.StoredFiles.AddRange(
            new StoredFile
            {
                Id = 1_818_001,
                PortfolioId = PortfolioId,
                FileName = "older-id-receipt.pdf",
                FilePath = "accounting/tie/older-id-receipt.pdf",
                ContentType = "application/pdf",
                FileSize = 128,
                EntityType = "Expense",
                EntityId = expense.Id,
                UploadedAt = uploadedAt,
            },
            new StoredFile
            {
                Id = 1_818_002,
                PortfolioId = PortfolioId,
                FileName = "higher-id-receipt.png",
                FilePath = "accounting/tie/higher-id-receipt.png",
                ContentType = "image/png",
                FileSize = 256,
                EntityType = "Expense",
                EntityId = expense.Id,
                UploadedAt = uploadedAt,
            });

        var lowerBank = new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Plaid",
            InstitutionName = "Lower Id Bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        // The production schema normally prevents duplicate matched expense targets. Remove that
        // guard only from this isolated test database to exercise legacy duplicate facts at the read
        // boundary, where equal timestamps must still resolve deterministically.
        await using (var adminConnection = new NpgsqlConnection(_context.ConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var dropIndex = adminConnection.CreateCommand();
            dropIndex.CommandText = "DROP INDEX \"IX_BankTransactions_PortfolioId_MatchedExpenseId\";";
            await dropIndex.ExecuteNonQueryAsync();
        }
        var higherBank = new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Plaid",
            InstitutionName = "Higher Id Bank",
            AccountName = "Checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.BankConnections.AddRange(lowerBank, higherBank);
        _db.SaveChanges();

        var postedAt = now.AddMinutes(-30);
        _db.BankTransactions.AddRange(
            new BankTransaction
            {
                Id = 1_828_001,
                PortfolioId = PortfolioId,
                BankConnectionId = lowerBank.Id,
                ProviderTransactionId = "tie-lower-id",
                PostedAt = postedAt,
                Description = "Lower id match",
                MerchantName = "Lower",
                Amount = -225m,
                IsoCurrencyCode = "USD",
                Category = "Withdrawal",
                MatchStatus = "Matched",
                MatchedExpenseId = expense.Id,
                CreatedAt = postedAt,
                UpdatedAt = postedAt,
            },
            new BankTransaction
            {
                Id = 1_828_002,
                PortfolioId = PortfolioId,
                BankConnectionId = higherBank.Id,
                ProviderTransactionId = "tie-higher-id",
                PostedAt = postedAt,
                Description = "Higher id match",
                MerchantName = "Higher",
                Amount = -225m,
                IsoCurrencyCode = "USD",
                Category = "Withdrawal",
                MatchStatus = "Matched",
                MatchedExpenseId = expense.Id,
                CreatedAt = postedAt,
                UpdatedAt = postedAt,
            });
        _db.SaveChanges();
        _commands.Clear();

        var result = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery
            {
                Kind = "Expense",
                Take = 20,
            },
            CancellationToken.None);

        var row = result.Items.Should().ContainSingle(item => item.Id == expense.Id).Subject;
        row.HasReceipt.Should().BeTrue();
        row.ReceiptIsImage.Should().BeTrue("the higher-id receipt wins an UploadedAt tie");
        row.Reconciled.Should().BeTrue();
        row.ClearedBankName.Should().Be("Higher Id Bank");
        row.ClearedAt.Should().Be(postedAt);

        _commands.Should().HaveCount(3);
        var hydrationSql = _commands.Single(sql => sql.Contains("\"StoredFiles\""));
        hydrationSql.Should().Contain("ORDER BY")
            .And.Contain("\"UploadedAt\" DESC")
            .And.Contain("\"Id\" DESC");
        hydrationSql.Should().Contain("\"PostedAt\" DESC")
            .And.Contain("\"Id\" DESC");
    }

    [Fact]
    public async Task GetTransactionsAsync_DefaultSeedIsSlimAndHydratesOnlyPageBranches()
    {
        var now = new DateTime(2027, 2, 18, 12, 0, 0, DateTimeKind.Utc);
        var (_, lease) = SeedPropertyAndLease(now);
        SeedPayment(lease, 1_100m, dueDate: now, paidInFull: false);
        _commands.Clear();

        var result = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { Take = 20 },
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        _commands.Should().HaveCount(3);
        foreach (var seedSql in _commands.Take(2))
        {
            seedSql.Should()
                .Contain("authorized_accounts AS MATERIALIZED")
                .And.Contain("UNION ALL")
                .And.NotContain("vw_lease_management_lifecycle")
                .And.NotContain("\"Vendors\"")
                .And.NotContain("\"WorkOrders\"")
                .And.NotContain("\"StoredFiles\"")
                .And.NotContain("\"BankTransactions\"",
                    "default count and top-N operate on normalized source keys, not display hydration");
        }

        var defaultHydrationSql = _commands[2];
        defaultHydrationSql.Should()
            .Contain("page_ledger_entries AS MATERIALIZED")
            .And.Contain("entry.\"Id\" = ANY(@ledgerIds::bigint[])")
            .And.Contain("page_lifecycle AS MATERIALIZED")
            .And.Contain("LEFT JOIN page_lifecycle AS lifecycle")
            .And.NotContain("LEFT JOIN vw_lease_management_lifecycle AS lifecycle")
            .And.NotContain("page_expenses AS MATERIALIZED")
            .And.NotContain("page_application_entries AS MATERIALIZED",
                "branches without page keys are omitted from the hydration statement");

        _commands.Clear();
        var empty = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { Kind = "Bank", Take = 20 },
            CancellationToken.None);

        empty.Items.Should().BeEmpty();
        empty.TotalCount.Should().Be(0);
        _commands.Should().HaveCount(3, "empty pages retain the fixed statement budget");
        _commands[2].Should().Contain("WHERE FALSE",
            "empty hydration is one typed SQL statement rather than a skipped round trip");
    }

    [Fact]
    public async Task GetTransactionsAsync_PreservesEveryWhitelistedSortInSql()
    {
        var day1 = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);
        var day4 = day1.AddDays(3);
        var (property, lease) = SeedPropertyAndLease(day3);
        SeedPayment(lease, 300m, dueDate: day3, paidInFull: false);
        var expense = SeedExpense(
            "Alpha expense",
            100m,
            day2,
            ScheduleECategory.Repairs,
            ExpenseStatus.Paid,
            property.Id);
        expense.UpdatedAt = day4;
        var applicationAccount = SeedApplicationFinancialAccount(property, day1);
        SeedApplicationFinancialEntry(
            applicationAccount,
            property,
            day1,
            "Zulu application",
            200m,
            ApplicationFinancialEntryType.FeeCollection,
            ApplicationFinancialDirection.Increase);
        _db.SaveChanges();

        var ascending = new Dictionary<string, string[]>
        {
            ["amount"] = ["TenantLedger", "Expense", "ApplicationFee"],
            ["description"] = ["Expense", "TenantLedger", "ApplicationFee"],
            ["category"] = ["ApplicationFee", "TenantLedger", "Expense"],
            ["status"] = ["TenantLedger", "Expense", "ApplicationFee"],
            ["kind"] = ["ApplicationFee", "Expense", "TenantLedger"],
            ["date"] = ["ApplicationFee", "Expense", "TenantLedger"],
            ["createdat"] = ["ApplicationFee", "Expense", "TenantLedger"],
            ["updatedat"] = ["ApplicationFee", "TenantLedger", "Expense"],
        };

        foreach (var (sortField, expectedKinds) in ascending)
        {
            foreach (var descending in new[] { false, true })
            {
                _commands.Clear();
                var result = await _sut.GetTransactionsAsync(
                    _scope,
                    new AccountingTransactionsQuery
                    {
                        Sort = descending ? $"-{sortField}" : sortField,
                        Take = 20,
                    },
                    CancellationToken.None);

                result.Items.Select(row => row.Kind).Should().Equal(
                    descending ? expectedKinds.Reverse() : expectedKinds,
                    $"{sortField} {(descending ? "descending" : "ascending")} must retain the existing union order");
                _commands.Should().HaveCount(3);
                _commands[1].Should()
                    .Contain("row_number() OVER")
                    .And.Contain("ORDER BY")
                    .And.Contain("OFFSET @skip")
                    .And.Contain("LIMIT @take",
                        "every whitelisted sort and page boundary must run in PostgreSQL");
            }
        }

        var mixedHydrationSql = _commands[2];
        mixedHydrationSql.Should()
            .Contain("page_ledger_entries AS MATERIALIZED")
            .And.Contain("entry.\"Id\" = ANY(@ledgerIds::bigint[])")
            .And.Contain("page_expenses AS MATERIALIZED")
            .And.Contain("expense.\"Id\" = ANY(@expenseIds::integer[])")
            .And.Contain("page_application_entries AS MATERIALIZED")
            .And.Contain("application_entry.\"Id\" = ANY(@applicationIds::integer[])")
            .And.Contain("page_keys AS MATERIALIZED")
            .And.Contain("UNION ALL",
                "a mixed page hydrates each source only after its branch-local page predicate");
        mixedHydrationSql.IndexOf(
            "entry.\"Id\" = ANY(@ledgerIds::bigint[])",
            StringComparison.Ordinal).Should().BeLessThan(
                mixedHydrationSql.IndexOf("INNER JOIN authorized_accounts", StringComparison.Ordinal));
        var expensePredicateIndex = mixedHydrationSql.IndexOf(
            "expense.\"Id\" = ANY(@expenseIds::integer[])",
            StringComparison.Ordinal);
        expensePredicateIndex.Should().BeLessThan(mixedHydrationSql.IndexOf(
            "INNER JOIN authorized_properties",
            expensePredicateIndex,
            StringComparison.Ordinal));
        var applicationPredicateIndex = mixedHydrationSql.IndexOf(
            "application_entry.\"Id\" = ANY(@applicationIds::integer[])",
            StringComparison.Ordinal);
        applicationPredicateIndex.Should().BeLessThan(mixedHydrationSql.IndexOf(
            "INNER JOIN authorized_properties",
            applicationPredicateIndex,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransactionsAsync_PreservesEveryFilterAndSearchSurfaceInSql()
    {
        var day3 = DateTime.UtcNow.Date.AddHours(12);
        var day2 = day3.AddDays(-1);
        var day1 = day3.AddDays(-2);
        var day5 = day3.AddDays(2);
        var (property, lease) = SeedPropertyAndLease(day3);
        var payment = SeedPayment(lease, 300m, dueDate: day3, paidInFull: true, paidDate: day3);
        var charge = _db.TenantLedgerEntries.Single(entry =>
            entry.TenantAccountId == payment.TenantAccountId &&
            entry.EntryType == TenantLedgerEntryType.RentCharge);

        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Acme vendor",
            ServiceType = "Repairs",
            CreatedAt = day2,
            UpdatedAt = day2,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = lease.LeaseManagement!.UnitId,
            Title = "Boiler work",
            Description = "Boiler work",
            Category = "General",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = day2,
            UpdatedAt = day2,
        };
        _db.AddRange(vendor, workOrder);
        _db.SaveChanges();
        var expense = SeedExpense(
            "Filterable expense",
            100m,
            day2,
            ScheduleECategory.Repairs,
            ExpenseStatus.Paid,
            property.Id,
            save: false);
        expense.OperationalScope = ExpenseOperationalScope.WorkOrder;
        expense.UnitId = lease.LeaseManagement.UnitId;
        expense.VendorId = vendor.Id;
        expense.WorkOrderId = workOrder.Id;
        expense.Notes = "expense note 818";
        _db.SaveChanges();

        var applicationAccount = SeedApplicationFinancialAccount(property, day1);
        var applicationEntry = SeedApplicationFinancialEntry(
            applicationAccount,
            property,
            day1,
            "Application fee",
            200m,
            ApplicationFinancialEntryType.FeeCollection,
            ApplicationFinancialDirection.Increase,
            providerReference: "provider-filter-818");
        var otherProperty = SeedProperty("Other", day5);
        var otherExpense = SeedExpense(
            "Other property expense",
            50m,
            day5,
            ScheduleECategory.Supplies,
            ExpenseStatus.Pending,
            otherProperty.Id);

        var cases = new (string Name, AccountingTransactionsQuery Query, (string Kind, long Id)[] Expected)[]
        {
            ("kind", new() { Kind = "payment", Take = 20 }, [("Payment", payment.Id)]),
            ("status", new() { Status = "debit", Take = 20 }, [("TenantLedger", charge.Id)]),
            ("category", new() { Category = "repairs", Take = 20 }, [("Expense", expense.Id)]),
            ("property", new() { PropertyId = property.Id, Take = 20 },
                [("TenantLedger", charge.Id), ("Payment", payment.Id), ("Expense", expense.Id), ("ApplicationFee", applicationEntry.Id)]),
            ("date", new() { From = day2.Date, To = day2.Date, Take = 20 }, [("Expense", expense.Id)]),
            ("description search", new() { Search = "Filterable expense", Take = 20 }, [("Expense", expense.Id)]),
            ("property search", new() { Search = property.Name, Take = 20 },
                [("TenantLedger", charge.Id), ("Payment", payment.Id), ("Expense", expense.Id), ("ApplicationFee", applicationEntry.Id)]),
            ("tenant search", new() { Search = "Maria Tenant", Take = 20 },
                [("TenantLedger", charge.Id), ("Payment", payment.Id)]),
            ("account search", new() { Search = lease.LeaseManagement.TenantAccount!.AccountNumber, Take = 20 },
                [("TenantLedger", charge.Id), ("Payment", payment.Id)]),
            ("vendor search", new() { Search = vendor.Name, Take = 20 }, [("Expense", expense.Id)]),
            ("work-order search", new() { Search = workOrder.Title, Take = 20 }, [("Expense", expense.Id)]),
            ("notes search", new() { Search = "expense note 818", Take = 20 }, [("Expense", expense.Id)]),
            ("application label search", new() { Search = "Rental application", Take = 20 }, [("ApplicationFee", applicationEntry.Id)]),
            ("application reference search", new() { Search = "provider-filter-818", Take = 20 }, [("ApplicationFee", applicationEntry.Id)]),
            ("other property exclusion", new() { PropertyId = otherProperty.Id, Take = 20 }, [("Expense", otherExpense.Id)]),
        };

        foreach (var testCase in cases)
        {
            _commands.Clear();
            var result = await _sut.GetTransactionsAsync(_scope, testCase.Query, CancellationToken.None);

            result.Items.Select(row => (row.Kind, row.Id)).Should().BeEquivalentTo(
                testCase.Expected,
                $"the {testCase.Name} filter must retain the existing union membership");
            _commands.Should().HaveCount(3);
            _commands.Take(2).Should().OnlyContain(sql =>
                sql.Contains("filtered_seed", StringComparison.Ordinal) &&
                sql.Contains("transaction_seed", StringComparison.Ordinal),
                "filtering must be applied to the normalized union in both count and page statements");
        }

        vendor.DeletedAt = day3.AddMinutes(1);
        workOrder.DeletedAt = day3.AddMinutes(1);
        _db.SaveChanges();

        _commands.Clear();
        var rowsAfterSourceDeletion = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { PropertyId = property.Id, Take = 20 },
            CancellationToken.None);
        var vendorSearchAfterDeletion = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { Search = vendor.Name, Take = 20 },
            CancellationToken.None);
        var workOrderSearchAfterDeletion = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { Search = workOrder.Title, Take = 20 },
            CancellationToken.None);

        using (new AssertionScope())
        {
            rowsAfterSourceDeletion.Items
                .Single(row => row.Kind == "Expense" && row.Id == expense.Id)
                .Counterparty.Should().BeNull(
                    "soft-deleted vendors must retain the prior EF query-filter semantics during hydration");
            vendorSearchAfterDeletion.Items.Should().NotContain(
                row => row.Kind == "Expense" && row.Id == expense.Id,
                "soft-deleted vendor text must not match transaction search");
            workOrderSearchAfterDeletion.Items.Should().NotContain(
                row => row.Kind == "Expense" && row.Id == expense.Id,
                "soft-deleted work-order text must not match transaction search");
        }

        _commands.Clear();
        var signedRows = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery { PropertyId = property.Id, Take = 20 },
            CancellationToken.None);
        signedRows.Items.Single(row => row.Id == charge.Id && row.Kind == "TenantLedger")
            .Amount.Should().Be(-300m);
        signedRows.Items.Single(row => row.Id == payment.Id && row.Kind == "Payment")
            .Amount.Should().Be(300m);
        signedRows.Items.Single(row => row.Id == expense.Id && row.Kind == "Expense")
            .Amount.Should().Be(-100m);
        signedRows.Items.Single(row => row.Id == applicationEntry.Id && row.Kind == "ApplicationFee")
            .Amount.Should().Be(200m);
        _commands.Should().HaveCount(3);

        _commands.Clear();
        var selectedScope = _db.SeedPropertyManagerScope(
            PortfolioId,
            property.Id,
            "accounting-transaction-selected-property");
        await _context.ActivateApiScopeAsync(selectedScope);
        _commands.Clear();
        var selectedResult = await _sut.GetTransactionsAsync(
            selectedScope,
            new AccountingTransactionsQuery { Take = 20 },
            CancellationToken.None);

        selectedResult.Items.Select(row => (row.Kind, row.Id)).Should().BeEquivalentTo(
            new[]
            {
                ("TenantLedger", charge.Id),
                ("Payment", payment.Id),
                ("Expense", (long)expense.Id),
                ("ApplicationFee", (long)applicationEntry.Id),
            },
            "the same SQL authorization relation must exclude the other property's expense");
        _commands.Should().HaveCount(3);
        _commands.Take(2).Should().OnlyContain(sql =>
            sql.Contains("rc_api_effective_capability_scopes", StringComparison.Ordinal) &&
            sql.Contains("authorized_properties AS MATERIALIZED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransactionsAsync_PreservesApplicationFeeSignsAfterApplicationDeletionWithoutPii()
    {
        var now = new DateTime(2027, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        var account = SeedApplicationFinancialAccount(property, now, "Private", "Applicant");
        var collection = SeedApplicationFinancialEntry(
            account,
            property,
            now,
            "Application fee collected",
            125m,
            ApplicationFinancialEntryType.FeeCollection,
            ApplicationFinancialDirection.Increase);
        var refund = SeedApplicationFinancialEntry(
            account,
            property,
            now.AddMinutes(1),
            "Application fee refunded",
            50m,
            ApplicationFinancialEntryType.Refund,
            ApplicationFinancialDirection.Decrease,
            relatedEntryId: collection.Id);
        account.RentalApplication!.DeletedAt = now.AddMinutes(2);
        _db.SaveChanges();
        _commands.Clear();

        var result = await _sut.GetTransactionsAsync(
            _scope,
            new AccountingTransactionsQuery
            {
                Kind = "ApplicationFee",
                Sort = "date",
                Take = 20,
            },
            CancellationToken.None);

        result.Items.Select(row => (row.Id, row.Category, row.Amount)).Should().Equal(
            (collection.Id, "ApplicationFee", 125m),
            (refund.Id, "ApplicationFeeRefund", -50m));
        result.Items.Should().OnlyContain(row =>
            row.Kind == "ApplicationFee" &&
            row.Counterparty == "Rental application" &&
            row.DetailHref == "/applications");
        _commands.Should().HaveCount(3);
        _commands.Should().OnlyContain(sql =>
            !sql.Contains("\"RentalApplications\"", StringComparison.Ordinal),
            "immutable finance history must never rejoin a deleted application or recover applicant PII");
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
                Status = LoanPaymentStatus.Paid,
                PaidDate = new DateTime(year, 01, 01, 0, 0, 0, DateTimeKind.Utc),
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
                Status = LoanPaymentStatus.Paid,
                PaidDate = new DateTime(year, 02, 01, 0, 0, 0, DateTimeKind.Utc),
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
            sql.Contains("\"LoanPaymentCorrections\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "modeled mortgage interest must select the latest correction and aggregate in SQL");

        var scheduleERollupSql = _commands.Where(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LoanPayments", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LoanPaymentCorrections", StringComparison.OrdinalIgnoreCase) &&
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
    public async Task BankingListTransactions_SelectsEffectiveLoanSuggestionInTwoSqlStatements()
    {
        var now = new DateTime(2027, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var (property, _) = SeedPropertyAndLease(now);
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Lender = "Correction Bank",
            OriginalAmount = 200_000m,
            CurrentBalance = 124_963m,
            AnnualInterestRatePct = 4.5m,
            TermMonths = 360,
            StartDate = now.AddYears(-10),
            DayOfMonthDue = 20,
            MonthlyPrincipalInterest = 1_046m,
            MonthlyEscrow = 318m,
            Status = LoanStatus.Active,
            CreatedAt = now.AddYears(-10),
            UpdatedAt = now,
        };
        var payment = new LoanPayment
        {
            PortfolioId = PortfolioId,
            Loan = loan,
            PeriodKey = "2027-02",
            DueDate = now.AddDays(-8),
            InterestAmount = 470.23m,
            PrincipalAmount = 583.77m,
            EscrowAmount = 318m,
            TotalAmount = 1_372m,
            BalanceAfter = 124_810.23m,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = now.AddDays(-8),
        };
        _db.LoanPayments.Add(payment);
        _db.SaveChanges();
        _db.LoanPaymentCorrections.Add(new LoanPaymentCorrection
        {
            PortfolioId = PortfolioId,
            LoanPaymentId = payment.Id,
            AttemptId = Guid.NewGuid(),
            DueDate = now,
            PaidDate = now,
            InterestAmount = 615m,
            PrincipalAmount = 431m,
            EscrowAmount = 318m,
            TotalAmount = 1_364m,
            BalanceAfter = 124_963m,
            Status = LoanPaymentStatus.Paid,
            CreatedAtUtc = now,
        });
        var transaction = SeedBankTransaction(
            "CORRECTION BANK 2027-02",
            "Correction Bank",
            -1_364m,
            now,
            "Mortgage",
            "Unmatched");
        transaction.PropertyId = property.Id;
        _db.SaveChanges();

        var banking = new BankingService(
            _db,
            new EphemeralDataProtectionProvider(),
            Mock.Of<IPlaidBankingProvider>(),
            Mock.Of<IRequestWriteExecutor>(),
            Options.Create(new PlaidOptions()),
            TimeProvider.System);
        _commands.Clear();

        var result = await banking.ListTransactionsAsync(
            PortfolioId, "Unmatched", skip: 0, take: 10);

        result.Items.Should().ContainSingle();
        result.Items[0].SuggestedMatch.Should().NotBeNull();
        result.Items[0].SuggestedMatch!.EntityType.Should().Be("LoanPayment");
        result.Items[0].SuggestedMatch!.EntityId.Should().Be(payment.Id);
        _commands.Should().HaveCount(2,
            "Banking uses one count and one bounded reader with SQL-ranked suggestions");
        var readerSql = _commands.Single(sql =>
            sql.Contains("\"LoanPaymentCorrections\"", StringComparison.OrdinalIgnoreCase));
        readerSql.Should()
            .Contain("\"LoanPayments\"")
            .And.Contain("UNION ALL")
            .And.Contain("ORDER BY")
            .And.Contain("LIMIT");
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

    private ApplicationFinancialAccount SeedApplicationFinancialAccount(
        Property property,
        DateTime openedAt,
        string firstName = "Alex",
        string lastName = "Applicant")
    {
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            FirstName = firstName,
            LastName = lastName,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = openedAt,
            CreatedAt = openedAt,
            UpdatedAt = openedAt,
        };
        _db.RentalApplications.Add(application);
        _db.SaveChanges();

        var account = new ApplicationFinancialAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            RentalApplicationId = application.Id,
            RentalApplication = application,
            Currency = "USD",
            OpenedAtUtc = openedAt,
            CreatedByUserId = _scope.UserId,
        };
        _db.ApplicationFinancialAccounts.Add(account);
        _db.SaveChanges();
        return account;
    }

    private ApplicationFinancialEntry SeedApplicationFinancialEntry(
        ApplicationFinancialAccount account,
        Property property,
        DateTime occurredAt,
        string description,
        decimal amount,
        ApplicationFinancialEntryType entryType,
        ApplicationFinancialDirection direction,
        string? providerReference = null,
        int? relatedEntryId = null)
    {
        var entry = new ApplicationFinancialEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            ApplicationFinancialAccountId = account.Id,
            PropertyId = property.Id,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(occurredAt),
            OccurredAtUtc = occurredAt,
            Description = description,
            Method = "Card",
            Provider = providerReference == null ? null : "Test",
            ProviderReference = providerReference,
            Source = ApplicationFinancialEntrySource.Manual,
            IdempotencyKey = $"accounting-transaction:{Guid.NewGuid():N}",
            RelatedEntryId = relatedEntryId,
            CreatedByUserId = _scope.UserId,
        };
        _db.ApplicationFinancialEntries.Add(entry);
        _db.SaveChanges();
        return entry;
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
