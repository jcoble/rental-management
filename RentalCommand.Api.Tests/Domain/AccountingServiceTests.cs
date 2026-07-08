using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

// NOTE: GetTransactionsAsync (the accounting transactions grid) is covered by
// RentalCommand.IntegrationTests/AccountingTransactionsViewTests.cs, NOT here. It reads the
// vw_accounting_transactions Postgres VIEW and uses ILIKE — neither of which exists/works under the
// in-memory SQLite provider this class uses (EnsureCreated does not create raw-SQL-migration views,
// and SQLite has no ILIKE). Those tests run against a real Postgres (Testcontainers) and self-skip
// when Docker is unavailable. This SQLite class keeps the non-view accounting logic
// (summary / snapshot / past-due / reports / year-end), which translates on both providers.
public class AccountingServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly AccountingService _sut;

    public AccountingServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new AccountingServiceTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new AccountingService(_db, new ScheduleEService(_db), new YearEndPacketPdfGenerator(), TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsPlainEnglishMoneySnapshot()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        SeedPropertyLeaseAndPayment(now);
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: now,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid);

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);

        summary.Snapshot.Title.Should().Be("Overdue rent needs attention");
        summary.Snapshot.Summary.Should().NotBeNullOrWhiteSpace();
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("overdue"));
        summary.Snapshot.Bullets.Should().Contain(b => b.Contains("Repairs"));
    }

    [Fact]
    public async Task GetSummaryAsync_OrdersAndTotalsExpenseCategoriesInSql()
    {
        var now = new DateTime(2026, 05, 25, 12, 0, 0, DateTimeKind.Utc);
        SeedExpense("Minor repair", 40m, now, ScheduleECategory.Repairs, ExpenseStatus.Paid);
        SeedExpense("Insurance premium", 125m, now, ScheduleECategory.Insurance, ExpenseStatus.Paid);
        SeedExpense("Cleaning", 75m, now, ScheduleECategory.CleaningMaintenance, ExpenseStatus.Paid);

        _commands.Clear();

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);

        summary.ExpensesByCategory.Select(c => c.Category).Should().Equal(
            ScheduleECategory.Insurance,
            ScheduleECategory.CleaningMaintenance,
            ScheduleECategory.Repairs);
        summary.TotalExpenses.Should().Be(240m);

        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase),
            "expense category ordering must run in SQL before materialization");
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "the total expense aggregate must be computed by SQL instead of summing the materialized category rows");
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
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = inMonth,
            PaidDate = inMonth,
            Method = "Check",
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Past due: $1,200 rent overdue (due yesterday, still scheduled) on the same lease.
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200m,
            DueDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        // Spent this month: $456.88 in repairs paid.
        SeedExpense(
            description: "ComfortZone HVAC",
            amount: 456.88m,
            incurredAt: inMonth,
            category: ScheduleECategory.Repairs,
            status: ExpenseStatus.Paid,
            propertyId: property.Id);

        var snapshot = await _sut.GetSnapshotAsync(PortfolioId, CancellationToken.None);

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

        _db.Payments.AddRange(
            new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Paid,
                Amount = 1200m,
                DueDate = now,
                PaidDate = now,
                Method = "Check",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.SecurityDeposit,
                Status = PaymentStatus.Paid,
                Amount = 1200m,
                DueDate = now,
                PaidDate = now,
                Method = "Check",
                CreatedAt = now,
                UpdatedAt = now,
            });
        _db.SaveChanges();

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);
        var snapshot = await _sut.GetSnapshotAsync(PortfolioId, CancellationToken.None);
        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

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
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = leaseA,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1000m,
            DueDate = now.AddDays(-10),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = leaseA,
            PaymentType = PaymentType.LateFee,
            Status = PaymentStatus.Late,
            Amount = 200m,
            DueDate = now.AddDays(-3),
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Lease B: one past-due payment ($800) → a second behind tenant.
        var (_, leaseB) = SeedPropertyAndLease(now);
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = leaseB,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 800m,
            DueDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Lease B also has a paid payment and a future-scheduled one — neither is "behind".
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = leaseB,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 800m,
            DueDate = now.AddDays(-31),
            PaidDate = now.AddDays(-30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = leaseB,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 800m,
            DueDate = now.AddDays(15),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        _commands.Clear();

        var snapshot = await _sut.GetSnapshotAsync(PortfolioId, CancellationToken.None);
        var pastDue = await _sut.GetPastDueAsync(PortfolioId, CancellationToken.None);

        // The KPI count equals the number of list rows (two distinct behind leases), and the amounts agree.
        snapshot.PastDueCount.Should().Be(2);
        pastDue.TotalCount.Should().Be(2);
        pastDue.Items.Should().HaveCount(2);
        snapshot.PastDueCount.Should().Be(pastDue.TotalCount);

        snapshot.PastDueAmount.Should().Be(2000m); // 1000 + 200 + 800
        pastDue.TotalPastDueAmount.Should().Be(snapshot.PastDueAmount);

        // Lease A's row rolls up both of its past-due payments into one tenant.
        var rowA = pastDue.Items.Single(i => i.LeaseId == leaseA.Id);
        rowA.OverduePaymentCount.Should().Be(2);
        rowA.PastDueAmount.Should().Be(1200m);

        // Ordered by who's waited longest (oldest due date first) → Lease A leads.
        pastDue.Items.First().LeaseId.Should().Be(leaseA.Id);

        var sql = string.Join("\n---\n", _commands);
        sql.Should().Contain("COUNT", "past-due tenant counts must be aggregated in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("past-due balances must be summed in SQL");
        sql.Should().Contain("GROUP BY", "the SQL aggregate should be over the per-lease past-due grouping");
    }

    [Fact]
    public async Task GetPastDueAsync_ProjectsMetadataAndOldestPaymentInSingleRowQuery()
    {
        var now = DateTime.UtcNow;
        var (_, lease) = SeedPropertyAndLease(now);
        lease.Tenant!.Phone = "614-555-0130";

        var oldest = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 900m,
            DueDate = now.AddDays(-20),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var partial = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Partial,
            Amount = 500m,
            AmountPaid = 125m,
            DueDate = now.AddDays(-5),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Payments.AddRange(oldest, partial);
        _db.SaveChanges();
        _commands.Clear();

        var pastDue = await _sut.GetPastDueAsync(PortfolioId, CancellationToken.None);

        var row = pastDue.Items.Should().ContainSingle().Subject;
        row.LeaseId.Should().Be(lease.Id);
        row.TenantName.Should().Be("Maria Tenant");
        row.TenantPhone.Should().Be("614-555-0130");
        row.LeaseNumber.Should().Be("L-001");
        row.PropertyName.Should().Be("General");
        row.UnitNumber.Should().Be("12");
        row.UnitId.Should().Be(lease.UnitId, "the row carries the lease's unit so the oldest-payment link folds into the unit's Rent tab");
        row.OldestPaymentId.Should().Be(oldest.Id);
        row.OldestDueDate.Should().Be(oldest.DueDate);
        row.PastDueAmount.Should().Be(1275m);
        row.OverduePaymentCount.Should().Be(2);

        var selects = _commands
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        selects.Should().HaveCount(2, "past-due drill-down should run one summary query and one row projection query");
        selects[1].Should().Contain("\"Leases\"", "row metadata should be joined/projected with the past-due aggregate");
        selects[1].Should().Contain("\"Tenants\"", "tenant labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("\"Properties\"", "property labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("\"Units\"", "unit labels should not require a post-materialization dictionary query");
        selects[1].Should().Contain("ORDER BY", "oldest-payment selection and row ordering should be SQL-side");
    }

    [Fact]
    public async Task GetPastDueAsync_ExcludesEndedFixedTermLeasesFromActivePastDue()
    {
        var now = DateTime.UtcNow;

        var (_, endedLease) = SeedPropertyAndLease(now);
        endedLease.LeaseNumber = "L-ENDED";
        endedLease.StartDate = now.AddYears(-2);
        endedLease.EndDate = now.AddMonths(-1);
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = endedLease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late,
            Amount = 925m,
            DueDate = now.AddMonths(-6),
            CreatedAt = now,
            UpdatedAt = now,
        });

        var (_, currentLease) = SeedPropertyAndLease(now);
        currentLease.LeaseNumber = "L-CURRENT";
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = currentLease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late,
            Amount = 975m,
            DueDate = now.AddDays(-5),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();

        var snapshot = await _sut.GetSnapshotAsync(PortfolioId, CancellationToken.None);
        var pastDue = await _sut.GetPastDueAsync(PortfolioId, CancellationToken.None);

        snapshot.PastDueCount.Should().Be(1);
        snapshot.PastDueAmount.Should().Be(975m);
        pastDue.TotalCount.Should().Be(1);
        pastDue.TotalPastDueAmount.Should().Be(975m);
        pastDue.Items.Should().ContainSingle(i => i.LeaseId == currentLease.Id);
        pastDue.Items.Should().NotContain(i => i.LeaseId == endedLease.Id,
            "an ended fixed-term lease can keep historical ledger rows, but it should not be an active dashboard/Money TODO");
    }

    [Fact]
    public async Task GetReportsAsync_LedgerEntriesCarryPlainEnglishExplanations()
    {
        var now = new DateTime(2026, 03, 03, 12, 0, 0, DateTimeKind.Utc);
        var (_, lease) = SeedPropertyAndLease(now);

        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = now,
            Method = "Check",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        var paymentEntry = reports.Ledger.Single(l => l.Type == "Payment");
        paymentEntry.Explanation.Should().Be("Payment of $1,200 received by check on Mar 3.");
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

        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1_200m,
            DueDate = now,
            PaidDate = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
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

        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.Repairs &&
            c.Total == 1_000m);
        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.MortgageInterest &&
            c.Total == 1_190m);
        reports.ScheduleE.Should().Contain(c =>
            c.Category == ScheduleECategory.Depreciation &&
            c.Total == 8_727.27m);
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "regular Schedule E categories must be grouped and summed in SQL");
        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"LoanPayments\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "modeled mortgage interest must be grouped and summed in SQL");
    }

    [Fact]
    public async Task GetReportsAsync_BuildsLedgerWithSqlUnionAndOrdering()
    {
        var now = new DateTime(2026, 03, 03, 12, 0, 0, 0, DateTimeKind.Utc);
        var (property, lease) = SeedPropertyAndLease(now);

        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = now.AddDays(-2),
            PaidDate = now,
            Method = "Check",
            CreatedAt = now,
            UpdatedAt = now,
        });

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

        SeedBankTransaction(
            description: "Unmatched deposit",
            merchantName: "Tenant",
            amount: 150m,
            postedAt: now.AddDays(-3),
            category: "Deposit",
            matchStatus: "Unmatched");
        SeedBankTransaction(
            description: "Matched duplicate deposit",
            merchantName: "Tenant",
            amount: 1200m,
            postedAt: now.AddDays(-2),
            category: "Deposit",
            matchStatus: "Matched",
            matchedPaymentId: _db.Payments.Select(p => p.Id).Single());

        _commands.Clear();

        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        reports.Ledger.Should().Contain(l => l.Type == "Payment" && l.Amount == 1200m);
        reports.Ledger.Should().Contain(l => l.Type == "Expense" && l.Amount == -225m);
        reports.Ledger.Should().ContainSingle(l => l.Type == "Bank" && l.Description == "Unmatched deposit");
        reports.Ledger.Should().NotContain(l => l.Description == "Matched duplicate deposit");

        var ledgerSql = _commands.FirstOrDefault(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Payments\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        ledgerSql.Should().NotBeNull("the report ledger must filter, combine, and sort rows as one DB-side query");
        ledgerSql!.Should().Contain("ORDER BY", "ledger sorting must run in SQL");
        ledgerSql.Should().Contain("\"MatchedPaymentId\" IS NULL", "matched bank rows must be suppressed before materialization");
        ledgerSql.Should().Contain("\"MatchedExpenseId\" IS NULL", "matched bank rows must be suppressed before materialization");
    }

    [Fact]
    public async Task GetReportsAsync_ReturnsRecentLedgerPreviewAndTotalCount()
    {
        var now = new DateTime(2026, 07, 01, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            SeedBankTransaction(
                description: $"Deposit {i:D2}",
                merchantName: "Tenant",
                amount: 100m + i,
                postedAt: now.AddDays(i),
                category: "Deposit",
                matchStatus: "Unmatched");
        }

        _commands.Clear();

        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        reports.LedgerTotalCount.Should().Be(10);
        reports.RecentLedger.Should().HaveCount(8);
        reports.Ledger.Should().HaveCount(8);
        reports.RecentLedger[0].Description.Should().Be("Deposit 09");
        reports.RecentLedger[^1].Description.Should().Be("Deposit 02");
        _commands.Should().Contain(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
            "the report ledger preview must be limited by the database, not sliced in the web page");
    }

    [Fact]
    public async Task GetSummaryAndReports_CountUnmatchedBankActivityWithoutDoubleCountingMatchedRows()
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

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);
        var reports = await _sut.GetReportsAsync(PortfolioId, CancellationToken.None);

        summary.Payments.Collected.Should().Be(1200m);
        summary.TotalExpenses.Should().Be(84.25m);
        reports.TotalIncome.Should().Be(1200m);
        reports.TotalExpenses.Should().Be(84.25m);
        reports.Ledger.Should().ContainSingle(l => l.Type == "Bank" && l.Description == "Tenant ACH");
        reports.Ledger.Should().ContainSingle(l => l.Type == "Bank" && l.Description == "Hardware supply");
        reports.Ledger.Should().NotContain(l => l.Description == "Matched duplicate deposit");
    }

    [Fact]
    public async Task GetSummaryAndSnapshot_ExcludeConfirmedMatchedDeposit_ButCountDismissedDeposit()
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

        var summary = await _sut.GetSummaryAsync(PortfolioId, CancellationToken.None);
        var snapshot = await _sut.GetSnapshotAsync(PortfolioId, CancellationToken.None);

        // Only the dismissed $300 counts; the confirmed/matched $1,200 is excluded as already-recorded.
        summary.Payments.Collected.Should().Be(300m);
        snapshot.Collected.Should().Be(300m);
    }

    private (Property Property, Lease Lease) SeedPropertyAndLease(DateTime now)
    {
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
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-001",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return (property, lease);
    }

    private Property SeedPropertyLeaseAndPayment(DateTime now)
    {
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
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-001",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Scheduled,
            Amount = 1200m,
            DueDate = now.AddDays(-1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
        return property;
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
        int? matchedPaymentId = null,
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
            MatchedPaymentId = matchedPaymentId,
            MatchedExpenseId = matchedExpenseId,
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        };
        _db.BankTransactions.Add(transaction);
        _db.SaveChanges();
        return transaction;
    }
}

internal sealed class AccountingServiceTestDbContext : RentalCommandDbContext
{
    public AccountingServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().ToTable("Leases");
        modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
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
