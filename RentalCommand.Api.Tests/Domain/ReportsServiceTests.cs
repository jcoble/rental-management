using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Focused tests for the non-trivial Reports Hub calculations: aging buckets, the general-ledger and
/// rent-ledger running balances, occupancy %, cash-flow-by-month, deposit current-balance, and the
/// in-portfolio property filter (IDOR guard). SQLite does not enforce UTC-Kind, so these tests avoid
/// relying on any timezone quirk — they compare on whole amounts/days computed from Utc-kind dates.
/// </summary>
public class ReportsServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly ReportsService _sut;

    public ReportsServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new ReportsServiceTestDbContext(options);
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

        _sut = new ReportsService(_db, new OwnerStatementService(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    // ── Pure-function unit tests (no DB) ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "current")]
    [InlineData(30, "current")]
    [InlineData(31, "31to60")]
    [InlineData(60, "31to60")]
    [InlineData(61, "61to90")]
    [InlineData(90, "61to90")]
    [InlineData(91, "over90")]
    [InlineData(365, "over90")]
    public void AddToBucket_PlacesAmountInCorrectAgeBucket(int days, string expectedBucket)
    {
        var buckets = new DelinquencyBuckets();
        ReportsService.AddToBucket(buckets, days, 100m);

        switch (expectedBucket)
        {
            case "current": buckets.Current.Should().Be(100m); break;
            case "31to60": buckets.Days31To60.Should().Be(100m); break;
            case "61to90": buckets.Days61To90.Should().Be(100m); break;
            case "over90": buckets.Over90.Should().Be(100m); break;
        }

        var total = buckets.Current + buckets.Days31To60 + buckets.Days61To90 + buckets.Over90;
        total.Should().Be(100m, "the amount lands in exactly one bucket");
    }

    [Fact]
    public void AgeInDays_FloorsAtZeroForFutureDueDates()
    {
        var asOf = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        ReportsService.AgeInDays(asOf.AddDays(5), asOf).Should().Be(0);
        ReportsService.AgeInDays(asOf.AddDays(-45), asOf).Should().Be(45);
    }

    [Fact]
    public void Percent_ComputesOccupancyAndGuardsAgainstZeroUnits()
    {
        ReportsService.Percent(3, 4).Should().Be(75m);
        ReportsService.Percent(0, 0).Should().Be(0m, "no units → 0%, never a divide-by-zero");
        ReportsService.Percent(1, 3).Should().Be(33.3m, "rounded to one decimal");
    }

    [Fact]
    public void EnumerateMonths_CoversEveryCalendarMonthInclusive()
    {
        var from = new DateTime(2026, 01, 15, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 03, 02, 0, 0, 0, DateTimeKind.Utc);

        var months = ReportsService.EnumerateMonths(from, to).ToList();

        months.Should().Equal((2026, 1), (2026, 2), (2026, 3));
    }

    [Fact]
    public void ResolveRange_DefaultsToYearToDate_AndMakesToInclusiveOfTheDay()
    {
        // Explicit `to` at midnight is extended to end-of-day so a same-day charge still matches.
        var (from, to) = ReportsService.ResolveRange(new ReportRangeQuery
        {
            From = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 01, 31, 0, 0, 0, DateTimeKind.Utc),
        });

        from.Should().Be(new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc));
        to.Date.Should().Be(new DateTime(2026, 01, 31));
        to.TimeOfDay.Should().BeGreaterThan(TimeSpan.FromHours(23), "the closing day is inclusive");
    }

    // ── Aging buckets (DB) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDelinquencyAsync_BucketsOverdueByAge_AndExcludesPaidAndFuture()
    {
        var now = DateTime.UtcNow;
        var lease = SeedLease(SeedProperty("Maple"), SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // 10 days overdue → Current bucket.
        SeedPayment(lease, 1000m, dueDate: now.AddDays(-10), PaymentStatus.Scheduled);
        // 45 days overdue → 31-60.
        SeedPayment(lease, 500m, dueDate: now.AddDays(-45), PaymentStatus.Late);
        // 120 days overdue → 90+.
        SeedPayment(lease, 250m, dueDate: now.AddDays(-120), PaymentStatus.Partial);
        // Paid → excluded.
        SeedPayment(lease, 999m, dueDate: now.AddDays(-15), PaymentStatus.Paid, paidDate: now.AddDays(-14));
        // Future due → excluded.
        SeedPayment(lease, 800m, dueDate: now.AddDays(10), PaymentStatus.Scheduled);

        var report = await _sut.GetDelinquencyAsync(PortfolioId, new ReportRangeQuery(), CancellationToken.None);

        var row = report.Rows.Should().ContainSingle().Subject;
        row.Buckets.Current.Should().Be(1000m);
        row.Buckets.Days31To60.Should().Be(500m);
        row.Buckets.Over90.Should().Be(250m);
        row.Buckets.Days61To90.Should().Be(0m);
        row.Total.Should().Be(1750m);
        row.OldestOverdueDays.Should().BeGreaterThanOrEqualTo(120);

        report.Totals.Current.Should().Be(1000m);
        report.Totals.Days31To60.Should().Be(500m);
        report.Totals.Over90.Should().Be(250m);
        report.TotalOutstanding.Should().Be(1750m);
    }

    // ── General Ledger running balance (DB) ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGeneralLedgerAsync_ComputesRunningBalance_IncomePositiveExpenseNegative()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Jan 5: +1000 rent collected. Jan 10: -300 expense. Jan 20: +200 rent collected.
        SeedPayment(lease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedExpense(property.Id, 300m, paidAt: D(2026, 1, 10));
        SeedPayment(lease, 200m, dueDate: D(2026, 1, 15), PaymentStatus.Paid, paidDate: D(2026, 1, 20));

        var report = await _sut.GetGeneralLedgerAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
        }, CancellationToken.None);

        report.Entries.Should().HaveCount(3);
        // Ordered by date → running balance 1000, 700, 900.
        report.Entries[0].RunningBalance.Should().Be(1000m);
        report.Entries[1].RunningBalance.Should().Be(700m);
        report.Entries[2].RunningBalance.Should().Be(900m);

        report.Entries[1].Amount.Should().Be(-300m, "expenses are negative on the ledger");
        report.TotalIncome.Should().Be(1200m);
        report.TotalExpense.Should().Be(300m);
        report.ClosingBalance.Should().Be(900m);
    }

    // ── Rent Ledger running balance (DB) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRentLedgerAsync_AccruesChargesAndAppliesPayments_RunningBalance()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Charge 1000 due Mar 1 (still owed), pay 1000 on Mar 3. Charge 1000 due Apr 1 (owed).
        SeedPayment(lease, 1000m, dueDate: D(2026, 3, 1), PaymentStatus.Paid, paidDate: D(2026, 3, 3));
        SeedPayment(lease, 1000m, dueDate: D(2026, 4, 1), PaymentStatus.Late);

        var report = await _sut.GetRentLedgerAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 12, 31),
        }, CancellationToken.None);

        var ledger = report.Leases.Should().ContainSingle().Subject;
        // Entries: Mar 1 charge (+1000 → bal 1000), Mar 3 payment (-1000 → bal 0), Apr 1 charge (+1000 → bal 1000).
        ledger.Entries.Should().HaveCount(3);
        ledger.Entries[0].Balance.Should().Be(1000m);
        ledger.Entries[1].Balance.Should().Be(0m);
        ledger.Entries[2].Balance.Should().Be(1000m);

        ledger.TotalCharged.Should().Be(2000m);
        ledger.TotalPaid.Should().Be(1000m);
        ledger.Balance.Should().Be(1000m);

        report.TotalCharged.Should().Be(2000m);
        report.TotalPaid.Should().Be(1000m);
        report.TotalBalance.Should().Be(1000m);
    }

    [Fact]
    public async Task GetRentLedgerAsync_ChargeBeforePaymentSameDay_NeverGoesNegativeTransiently()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Charged and paid the SAME day.
        SeedPayment(lease, 1000m, dueDate: D(2026, 5, 1), PaymentStatus.Paid, paidDate: D(2026, 5, 1));

        var report = await _sut.GetRentLedgerAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 5, 1),
            To = D(2026, 5, 31),
        }, CancellationToken.None);

        var ledger = report.Leases.Should().ContainSingle().Subject;
        ledger.Entries[0].Type.Should().Be("Charge", "the charge is ordered before the same-day payment");
        ledger.Entries[0].Balance.Should().Be(1000m);
        ledger.Entries[1].Balance.Should().Be(0m);
        ledger.Balance.Should().Be(0m);
    }

    // ── Cash flow by month (DB) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCashFlowAsync_GroupsByMonth_FillsGapsWithZero_AndComputesNet()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Jan: +1000 income, -200 expense. Feb: nothing. Mar: +500 income.
        SeedPayment(lease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedExpense(property.Id, 200m, paidAt: D(2026, 1, 20));
        SeedPayment(lease, 500m, dueDate: D(2026, 3, 1), PaymentStatus.Paid, paidDate: D(2026, 3, 4));

        var report = await _sut.GetCashFlowAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 3, 31),
        }, CancellationToken.None);

        report.Months.Should().HaveCount(3);

        var jan = report.Months.Single(m => m.MonthKey == "2026-01");
        jan.Income.Should().Be(1000m);
        jan.Expense.Should().Be(200m);
        jan.Net.Should().Be(800m);

        var feb = report.Months.Single(m => m.MonthKey == "2026-02");
        feb.Income.Should().Be(0m);
        feb.Expense.Should().Be(0m);
        feb.Net.Should().Be(0m, "a month with no activity is present and zeroed, not missing");

        var mar = report.Months.Single(m => m.MonthKey == "2026-03");
        mar.Income.Should().Be(500m);

        report.TotalIncome.Should().Be(1500m);
        report.TotalExpense.Should().Be(200m);
        report.TotalNet.Should().Be(1300m);
    }

    // ── Occupancy % (DB) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOccupancyAsync_CountsOccupiedVacant_AndComputesPercentPerPropertyAndPortfolio()
    {
        var maple = SeedProperty("Maple");
        SeedUnit("1", maple.Id, UnitStatus.Occupied);
        SeedUnit("2", maple.Id, UnitStatus.Occupied);
        SeedUnit("3", maple.Id, UnitStatus.Vacant);
        SeedUnit("4", maple.Id, UnitStatus.Offline); // counts as not-occupied

        var oak = SeedProperty("Oak");
        SeedUnit("A", oak.Id, UnitStatus.Occupied);

        var report = await _sut.GetOccupancyAsync(PortfolioId, new ReportRangeQuery(), CancellationToken.None);

        var mapleRow = report.Rows.Single(r => r.PropertyName == "Maple");
        mapleRow.TotalUnits.Should().Be(4);
        mapleRow.OccupiedUnits.Should().Be(2);
        mapleRow.VacantUnits.Should().Be(2);
        mapleRow.OccupancyPercent.Should().Be(50m);

        report.TotalUnits.Should().Be(5);
        report.OccupiedUnits.Should().Be(3);
        report.VacantUnits.Should().Be(2);
        report.OccupancyPercent.Should().Be(60m);
    }

    // ── Security deposit current balance (DB) ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetSecurityDepositRegisterAsync_ComputesCurrentBalanceFromDeductionsAndReturned()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Held 1500, 200 in deductions, 300 returned → 1000 still in trust.
        _db.SecurityDepositHoldings.Add(new SecurityDepositHolding
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            Amount = 1500m,
            Status = SecurityDepositStatus.PartiallyReturned,
            HeldAt = D(2025, 1, 1),
            ReturnedAt = D(2026, 1, 1),
            ReturnedAmount = 300m,
            DeductionsJson = "[{\"Reason\":\"Cleaning\",\"Amount\":200.0,\"Notes\":null}]",
            CreatedAt = D(2025, 1, 1),
            UpdatedAt = D(2026, 1, 1),
        });
        _db.SaveChanges();

        var report = await _sut.GetSecurityDepositRegisterAsync(PortfolioId, new ReportRangeQuery(), CancellationToken.None);

        var row = report.Rows.Should().ContainSingle().Subject;
        row.Held.Should().Be(1500m);
        row.Deductions.Should().Be(200m);
        row.Returned.Should().Be(300m);
        row.CurrentBalance.Should().Be(1000m);

        report.TotalHeld.Should().Be(1500m);
        report.TotalCurrentBalance.Should().Be(1000m);
    }

    [Fact]
    public void ParseDeductionsTotal_HandlesNullBlankAndMalformedJson()
    {
        ReportsService.ParseDeductionsTotal(null).Should().Be(0m);
        ReportsService.ParseDeductionsTotal("").Should().Be(0m);
        ReportsService.ParseDeductionsTotal("not json").Should().Be(0m);
        ReportsService.ParseDeductionsTotal("[{\"Reason\":\"X\",\"Amount\":50.0}]").Should().Be(50m);
        ReportsService.ParseDeductionsTotal("[{\"Reason\":\"A\",\"Amount\":50.0},{\"Reason\":\"B\",\"Amount\":25.5}]")
            .Should().Be(75.5m);
    }

    // ── Property filter IDOR guard (DB) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RentRoll_PropertyFilter_DropsOutOfPortfolioIds_AndScopesToRequestedProperty()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        SeedLease(maple, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);
        SeedLease(oak, SeedUnit("A"), SeedTenant("Bob", "Birch"), rent: 2000m);

        // Filter to Maple plus a bogus out-of-portfolio id → only Maple's lease.
        var report = await _sut.GetRentRollAsync(PortfolioId, new ReportRangeQuery
        {
            PropertyIds = [maple.Id, 99999],
        }, CancellationToken.None);

        report.Rows.Should().ContainSingle();
        report.Rows[0].PropertyName.Should().Be("Maple");
        report.TotalMonthlyRent.Should().Be(1000m);
    }

    [Fact]
    public async Task RentRoll_WhenAllRequestedIdsAreOutOfPortfolio_ReturnsNoRows()
    {
        var maple = SeedProperty("Maple");
        SeedLease(maple, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Only a bogus id → empty filter set → no rows (must NOT widen to the whole portfolio).
        var report = await _sut.GetRentRollAsync(PortfolioId, new ReportRangeQuery
        {
            PropertyId = 99999,
        }, CancellationToken.None);

        report.Rows.Should().BeEmpty();
        report.LeaseCount.Should().Be(0);
    }

    // ── Lease expirations (DB) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLeaseExpirationsAsync_ReturnsLeasesEndingWithinWindow_Only()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty("Maple");

        var soon = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m,
            start: now.AddMonths(-11), end: now.AddDays(20));
        SeedLease(property, SeedUnit("2"), SeedTenant("Bob", "Birch"), rent: 1500m,
            start: now.AddMonths(-2), end: now.AddDays(200)); // outside the 90-day window

        var report = await _sut.GetLeaseExpirationsAsync(PortfolioId, new ReportRangeQuery(), days: 90, CancellationToken.None);

        report.WindowDays.Should().Be(90);
        report.Rows.Should().ContainSingle();
        report.Rows[0].LeaseId.Should().Be(soon.Id);
        report.Rows[0].DaysUntilExpiry.Should().BeInRange(19, 21);
        report.TotalMonthlyRent.Should().Be(1000m);
    }

    [Fact]
    public void GetCatalog_GroupsReportsByCategory_WithKeysTitlesEndpointsAndParams()
    {
        var catalog = _sut.GetCatalog();

        catalog.Categories.Select(c => c.Key)
            .Should().Contain(new[] { "accounting", "rent-payments", "owners", "operations" });

        var all = catalog.Categories.SelectMany(c => c.Reports).ToList();
        all.Should().Contain(r => r.Key == "rent-roll" && r.Endpoint == "/api/v1/reports/rent-roll");

        // External reports deep-link to their existing endpoints and are flagged External.
        var scheduleE = all.Single(r => r.Key == "schedule-e");
        scheduleE.External.Should().BeTrue();
        scheduleE.Endpoint.Should().Be("/api/v1/accounting/schedule-e");

        all.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Description));
    }

    // ── Seed helpers ───────────────────────────────────────────────────────────────────────────────

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private Property SeedProperty(string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
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
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private Unit SeedUnit(string number, int? propertyId = null, UnitStatus status = UnitStatus.Vacant)
    {
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            PropertyId = propertyId ?? _db.Properties.First().Id,
            UnitNumber = number,
            MarketRent = 1000m,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Units.Add(unit);
        _db.SaveChanges();
        return unit;
    }

    private Tenant SeedTenant(string first, string last)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = first,
            LastName = last,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant;
    }

    private Lease SeedLease(
        Property property, Unit unit, Tenant tenant, decimal rent,
        DateTime? start = null, DateTime? end = null, LeaseStatus status = LeaseStatus.Active)
    {
        var now = DateTime.UtcNow;
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = $"L-{Guid.NewGuid():N}".Substring(0, 8),
            Status = status,
            StartDate = start ?? now.AddMonths(-1),
            EndDate = end ?? now.AddYears(1),
            MonthlyRent = rent,
            SecurityDeposit = rent,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

    private Payment SeedPayment(
        Lease lease, decimal amount, DateTime dueDate, PaymentStatus status,
        DateTime? paidDate = null, PaymentType type = PaymentType.Rent)
    {
        var now = DateTime.UtcNow;
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            PaymentType = type,
            Status = status,
            Amount = amount,
            DueDate = dueDate,
            PaidDate = paidDate,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Payments.Add(payment);
        _db.SaveChanges();
        return payment;
    }

    private Expense SeedExpense(int propertyId, decimal amount, DateTime paidAt)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Category = ScheduleECategory.Repairs,
            Description = "Repair",
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = paidAt,
            PaidAt = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _db.Expenses.Add(expense);
        _db.SaveChanges();
        return expense;
    }
}

/// <summary>
/// SQLite-compatible DbContext for the reports tests: strips jsonb column types and Postgres check
/// constraints / partial-index filters that SQLite cannot execute (same approach as the other domain
/// test contexts).
/// </summary>
internal sealed class ReportsServiceTestDbContext : RentalCommandDbContext
{
    public ReportsServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

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
