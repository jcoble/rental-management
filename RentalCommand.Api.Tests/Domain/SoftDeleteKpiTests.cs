using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Audit H-4 + M-7 regression coverage: once a lease is soft-deleted, its payments must no longer
/// count toward any financial KPI ("Overdue", "Collected/MTD", past-due amount/count) and must not
/// produce ghost "who's behind" rows. The root cause was that the money aggregates read the scalar
/// <c>Payment.LeaseId</c> FK and never triggered the Lease soft-delete filter; the fix gives Payment
/// (and every other required dependent of a soft-deletable principal) a query filter that walks the
/// required navigation, so a soft-deleted principal's dependents disappear everywhere.
///
/// These run on SQLite (same pattern as <see cref="AccountingServiceTests"/>); they assert the EF
/// query-filter behaviour. The DB-layer RLS backstop is proven separately by the Postgres
/// <c>RlsTenantIsolationTests</c>.
/// </summary>
public sealed class SoftDeleteKpiTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly AccountingService _accounting;
    private readonly DashboardService _dashboard;

    public SoftDeleteKpiTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new SoftDeleteKpiTestDbContext(options);
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

        _accounting = new AccountingService(_db, new ScheduleEService(_db), new YearEndPacketPdfGenerator(), TimeProvider.System);
        _dashboard = new DashboardService(_db, new RentalCommand.Api.Services.Auditing.AuditDescriber());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task SoftDeletedLease_PaymentsAreExcludedFromPastDueAndSnapshotAndDashboard()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var inMonth = monthStart.AddHours(6);

        // Lease that STAYS active: $900 overdue + $500 collected this month.
        var live = SeedLease(now, "L-LIVE");
        SeedPayment(live, PaymentStatus.Scheduled, 900m, dueDate: now.AddDays(-2));
        SeedPayment(live, PaymentStatus.Paid, 500m, dueDate: inMonth, paidDate: inMonth);

        // Lease that will be soft-deleted: $7,777 overdue + $1,000 collected this month.
        var doomed = SeedLease(now, "L-DOOMED");
        SeedPayment(doomed, PaymentStatus.Scheduled, 7_777m, dueDate: now.AddDays(-5));
        SeedPayment(doomed, PaymentStatus.Paid, 1_000m, dueDate: inMonth, paidDate: inMonth);
        _db.SaveChanges();

        // Baseline: both leases' payments count.
        var before = await _accounting.GetSnapshotAsync(PortfolioId);
        before.PastDueAmount.Should().Be(8_677m);  // 900 + 7777
        before.PastDueCount.Should().Be(2);
        before.Collected.Should().Be(1_500m);       // 500 + 1000

        // Act — soft-delete the doomed lease the same way LeaseService.DeleteAsync does.
        doomed.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Assert — the soft-deleted lease's payments vanish from every KPI surface.
        var snapshot = await _accounting.GetSnapshotAsync(PortfolioId);
        snapshot.PastDueAmount.Should().Be(900m, "the soft-deleted lease's $7,777 overdue must not count");
        snapshot.PastDueCount.Should().Be(1, "only the live lease is still behind");
        snapshot.Collected.Should().Be(500m, "the soft-deleted lease's $1,000 collected must not count");

        var summary = await _accounting.GetSummaryAsync(PortfolioId);
        summary.Payments.Overdue.Should().Be(900m);
        summary.Payments.OverdueCount.Should().Be(1);
        summary.Payments.Collected.Should().Be(500m);

        var pastDue = await _accounting.GetPastDueAsync(PortfolioId);
        pastDue.TotalCount.Should().Be(1);
        pastDue.Items.Should().ContainSingle().Which.LeaseId.Should().Be(live.Id);
        pastDue.Items.Should().NotContain(i => i.LeaseId == doomed.Id,
            "a soft-deleted lease must never appear as a ghost 'who's behind' row");
        pastDue.TotalPastDueAmount.Should().Be(900m);

        var dash = await _dashboard.GetDashboardAsync(PortfolioId);
        dash!.Accounting.OverdueAmount.Should().Be(900m,
            "the dashboard Overdue KPI must also exclude the soft-deleted lease");
    }

    [Fact]
    public async Task SoftDeletedLease_PaymentsAreInvisibleWhenQueriedDirectly()
    {
        // M-7: the Payment query filter (via the required Lease navigation) hides a soft-deleted
        // lease's payments even on a raw DbSet query with no WHERE clause on Lease.
        var now = DateTime.UtcNow;
        var lease = SeedLease(now, "L-X");
        SeedPayment(lease, PaymentStatus.Scheduled, 1_234m, dueDate: now.AddDays(-1));
        _db.SaveChanges();

        (await _db.Payments.CountAsync()).Should().Be(1);

        lease.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await _db.Payments.CountAsync()).Should()
            .Be(0, "a soft-deleted lease's payments must be filtered out of every Payment query");

        // ...but they still exist physically — IgnoreQueryFilters proves the row was not hard-deleted.
        (await _db.Payments.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EndedFixedTermLease_PaymentsStayHistoricalButDoNotCountAsDashboardOverdue()
    {
        var now = DateTime.UtcNow;

        var endedLease = SeedLease(now, "L-ENDED");
        endedLease.StartDate = now.AddYears(-2);
        endedLease.EndDate = now.AddMonths(-1);
        SeedPayment(endedLease, PaymentStatus.Late, 925m, dueDate: now.AddMonths(-6));

        var currentLease = SeedLease(now, "L-CURRENT");
        SeedPayment(currentLease, PaymentStatus.Late, 975m, dueDate: now.AddDays(-5));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var allPhysicalPayments = await _db.Payments.IgnoreQueryFilters().CountAsync();
        allPhysicalPayments.Should().Be(2, "the ended lease's old payment row should remain in the database");

        var dashboard = await _dashboard.GetDashboardAsync(PortfolioId);
        dashboard!.Accounting.OverdueAmount.Should().Be(975m,
            "dashboard TODO/KPI surfaces should only nag on current leases unless an explicit continuation exists");

        var summary = await _accounting.GetSummaryAsync(PortfolioId);
        summary.Payments.Overdue.Should().Be(975m);
        summary.Payments.OverdueCount.Should().Be(1);
    }

    [Fact]
    public async Task SoftDeletedExpense_LineItemsAreFilteredOut()
    {
        // M-7 generic: ExpenseLineItem (required dependent of soft-deletable Expense) follows the
        // same rule.
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Category = ScheduleECategory.Repairs,
            Description = "Roof",
            Amount = 300m,
            IncurredAt = now,
            Status = ExpenseStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            LineItems =
            {
                new ExpenseLineItem { Description = "Shingles", Amount = 300m },
            },
        };
        _db.Expenses.Add(expense);
        _db.SaveChanges();

        (await _db.ExpenseLineItems.CountAsync()).Should().Be(1);

        expense.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await _db.ExpenseLineItems.CountAsync()).Should()
            .Be(0, "a soft-deleted expense's line items must be filtered out");
        (await _db.ExpenseLineItems.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DashboardNetThisMonth_UsesExpensePaidDateFallbackLikeMoneySnapshot()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var paidThisMonth = monthStart.AddDays(2);
        var incurredLastMonth = monthStart.AddDays(-3);

        var lease = SeedLease(now, "L-NET");
        var property = lease.Property!;
        SeedPayment(lease, PaymentStatus.Paid, 100m, dueDate: paidThisMonth, paidDate: paidThisMonth);
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Category = ScheduleECategory.Repairs,
            Description = "Invoice incurred last month but paid this month",
            Amount = 25m,
            IncurredAt = incurredLastMonth,
            PaidAt = paidThisMonth,
            Status = ExpenseStatus.Paid,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();

        var snapshot = await _accounting.GetSnapshotAsync(PortfolioId);
        snapshot.Collected.Should().Be(100m);
        snapshot.Spent.Should().Be(25m);
        snapshot.Net.Should().Be(75m);

        var dashboard = await _dashboard.GetDashboardAsync(PortfolioId);
        dashboard!.Accounting.PaidThisMonthAmount.Should().Be(100m);
        dashboard.Accounting.ExpensesThisMonthAmount.Should().Be(25m);
        dashboard.Accounting.NetThisMonth.Should().Be(75m);
    }

    [Fact]
    public async Task DashboardNetThisMonth_IncludesUnmatchedBankCashMovementAndNonRentPaymentsButNotSecurityDeposits()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var inMonth = monthStart.AddDays(3);

        var lease = SeedLease(now, "L-CASH");
        SeedPayment(lease, PaymentStatus.Paid, 100m, dueDate: inMonth, paidDate: inMonth);
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.LateFee,
            Status = PaymentStatus.Paid,
            Amount = 20m,
            DueDate = inMonth,
            PaidDate = inMonth,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.SecurityDeposit,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = inMonth,
            PaidDate = inMonth,
            CreatedAt = now,
            UpdatedAt = now,
        });
        SeedBankTransaction(amount: 10m, postedAt: inMonth, matchStatus: "Unmatched");
        SeedBankTransaction(amount: -15m, postedAt: inMonth, matchStatus: "Unmatched");
        SeedBankTransaction(amount: 999m, postedAt: inMonth, matchStatus: "Removed");
        _db.SaveChanges();

        var snapshot = await _accounting.GetSnapshotAsync(PortfolioId);
        snapshot.Collected.Should().Be(130m);
        snapshot.Spent.Should().Be(15m);
        snapshot.Net.Should().Be(115m);

        var dashboard = await _dashboard.GetDashboardAsync(PortfolioId);
        dashboard!.Accounting.PaidThisMonthAmount.Should().Be(130m);
        dashboard.Accounting.ExpensesThisMonthAmount.Should().Be(15m);
        dashboard.Accounting.NetThisMonth.Should().Be(115m);
    }

    // ----- seed helpers -----

    private Property SeedProperty(DateTime now)
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
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private Lease SeedLease(DateTime now, string leaseNumber)
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
            LeaseNumber = leaseNumber,
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
        return lease;
    }

    private void SeedPayment(Lease lease, PaymentStatus status, decimal amount, DateTime dueDate, DateTime? paidDate = null)
    {
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = status,
            Amount = amount,
            DueDate = dueDate,
            PaidDate = paidDate,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private void SeedBankTransaction(decimal amount, DateTime postedAt, string matchStatus)
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

        _db.BankTransactions.Add(new BankTransaction
        {
            PortfolioId = PortfolioId,
            BankConnection = connection,
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            PostedAt = postedAt,
            Description = "Bank cash movement",
            Amount = amount,
            IsoCurrencyCode = "USD",
            MatchStatus = matchStatus,
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        });
    }
}

/// <summary>SQLite-compatible context: strips Postgres-only DDL, identical to the sibling test contexts.</summary>
internal sealed class SoftDeleteKpiTestDbContext : RentalCommandDbContext
{
    public SoftDeleteKpiTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<SecurityDepositHolding>().Property(e => e.DeductionsJson).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().ToTable("Leases");
        modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
        modelBuilder.Entity<Payment>()
            .HasIndex(p => new { p.LeaseId, p.PaymentType, p.PeriodKey })
            .IsUnique()
            .HasFilter(null);
        modelBuilder.Entity<AutopayEnrollment>()
            .HasIndex(e => e.LeaseId)
            .IsUnique()
            .HasFilter(null);
    }
}
