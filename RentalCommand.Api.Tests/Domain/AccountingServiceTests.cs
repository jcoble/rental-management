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

        _sut = new AccountingService(_db, new ScheduleEService(_db), new YearEndPacketPdfGenerator());
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
            sql.Contains("\"BankTransactions\"", StringComparison.OrdinalIgnoreCase));

        ledgerSql.Should().NotBeNull("the report ledger must filter, combine, and sort rows as one DB-side query");
        ledgerSql!.Should().Contain("ORDER BY", "ledger sorting must run in SQL");
        ledgerSql.Should().Contain("\"MatchedPaymentId\" IS NULL", "matched bank rows must be suppressed before materialization");
        ledgerSql.Should().Contain("\"MatchedExpenseId\" IS NULL", "matched bank rows must be suppressed before materialization");
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
