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
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly ReportsService _sut;

    public ReportsServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
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

        _sut = new ReportsService(_db, new OwnerStatementService(_db), new ScheduleEService(_db));
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

    [Fact]
    public async Task GetDelinquencyAsync_OrdersAndTotalsInSql()
    {
        var now = DateTime.UtcNow;
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        var older = SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1000m);
        var newer = SeedLease(maple, SeedUnit("2", maple.Id), SeedTenant("Bea", "Birch"), rent: 900m);
        var excluded = SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Cal", "Cedar"), rent: 800m);

        SeedPayment(older, 1000m, dueDate: now.AddDays(-75), PaymentStatus.Late);
        SeedPayment(newer, 900m, dueDate: now.AddDays(-10), PaymentStatus.Scheduled);
        SeedPayment(excluded, 800m, dueDate: now.AddDays(-120), PaymentStatus.Late);

        _executedSql.Clear();

        var report = await _sut.GetDelinquencyAsync(PortfolioId, new ReportRangeQuery
        {
            PropertyIds = [maple.Id],
        }, CancellationToken.None);

        report.Rows.Should().HaveCount(2);
        report.Rows[0].LeaseId.Should().Be(older.Id);
        report.Rows[1].LeaseId.Should().Be(newer.Id);
        report.TotalOutstanding.Should().Be(1900m);
        report.Totals.Current.Should().Be(900m);
        report.Totals.Days61To90.Should().Be(1000m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("GROUP BY", "delinquency buckets must be grouped per lease in SQL");
        sql.Should().Contain("ORDER BY", "delinquency row ordering must run in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("bucket and total amounts must be summed in SQL");
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

    [Fact]
    public async Task GetGeneralLedgerAsync_FiltersOrdersAndTotalsInSql()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        var mapleLease = SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1000m);
        var oakLease = SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Bob", "Birch"), rent: 2000m);

        SeedPayment(mapleLease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedPayment(mapleLease, 777m, dueDate: D(2026, 2, 1), PaymentStatus.Paid, paidDate: D(2026, 2, 5));
        SeedPayment(oakLease, 222m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedExpense(maple.Id, 300m, paidAt: D(2026, 1, 10));
        SeedExpense(maple.Id, 444m, paidAt: D(2026, 2, 10));
        SeedExpense(oak.Id, 111m, paidAt: D(2026, 1, 10));

        _executedSql.Clear();

        var report = await _sut.GetGeneralLedgerAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
            PropertyIds = [maple.Id],
        }, CancellationToken.None);

        report.Entries.Should().HaveCount(2);
        report.TotalIncome.Should().Be(1000m);
        report.TotalExpense.Should().Be(300m);
        report.ClosingBalance.Should().Be(700m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("UNION", "ledger entries must be combined before materialization");
        sql.Should().Contain("ORDER BY", "ledger ordering must run in SQL");
        sql.Should().Contain(">=", "the start date filter must run in SQL");
        sql.Should().Contain("<=", "the end date filter must run in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("ledger totals must be summed in SQL");
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

    [Fact]
    public async Task GetRentLedgerAsync_FiltersOrdersAndTotalsInSql()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        var mapleLease = SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1000m);
        var oakLease = SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Bob", "Birch"), rent: 2000m);

        SeedPayment(mapleLease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedPayment(mapleLease, 777m, dueDate: D(2026, 2, 1), PaymentStatus.Paid, paidDate: D(2026, 2, 5));
        SeedPayment(oakLease, 222m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));

        _executedSql.Clear();

        var report = await _sut.GetRentLedgerAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
            PropertyIds = [maple.Id],
        }, CancellationToken.None);

        var ledger = report.Leases.Should().ContainSingle().Subject;
        ledger.LeaseId.Should().Be(mapleLease.Id);
        ledger.Entries.Should().HaveCount(2);
        ledger.TotalCharged.Should().Be(1000m);
        ledger.TotalPaid.Should().Be(1000m);
        ledger.Balance.Should().Be(0m);
        report.TotalCharged.Should().Be(1000m);
        report.TotalPaid.Should().Be(1000m);
        report.TotalBalance.Should().Be(0m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("UNION", "charge and receipt rows must be combined before materialization");
        sql.Should().Contain("GROUP BY", "per-lease rent-ledger totals must be grouped in SQL");
        sql.Should().Contain("ORDER BY", "ledger ordering must run in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("rent-ledger totals must be summed in SQL");

        _executedSql.Should().Contain(command =>
            command.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (command.Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
             command.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase)),
            "rent-ledger running balances must be computed by the ordered activity SQL query, not by walking materialized rows");
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

    [Fact]
    public async Task GetCashFlowAsync_ExcludesDeposits_AndUsesAmountPaidForPartials()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Rent paid in full → 1000. A security deposit is NOT income (§7) → excluded.
        SeedPayment(lease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedPayment(lease, 1500m, dueDate: D(2026, 1, 2), PaymentStatus.Paid, paidDate: D(2026, 1, 2),
            type: PaymentType.SecurityDeposit);
        // A partial rent payment contributes only the collected cash (AmountPaid), not the full Amount.
        var partial = SeedPayment(lease, 1000m, dueDate: D(2026, 1, 10), PaymentStatus.Partial, paidDate: D(2026, 1, 10));
        partial.AmountPaid = 300m;
        _db.SaveChanges();
        // A late fee IS income.
        SeedPayment(lease, 50m, dueDate: D(2026, 1, 15), PaymentStatus.Paid, paidDate: D(2026, 1, 15),
            type: PaymentType.LateFee);

        var report = await _sut.GetCashFlowAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
        }, CancellationToken.None);

        // 1000 rent + 300 partial-collected + 50 late fee = 1350; the 1500 deposit is excluded.
        report.TotalIncome.Should().Be(1350m);
    }

    [Fact]
    public void GetCashFlow_IncomeQuery_AggregatesInSql_NotInMemory()
    {
        // Guard the §18 hard rule directly: the cash-flow income aggregation must be SQL GROUP BY, not a
        // materialize-then-group. Build the same shape the service runs and inspect the generated SQL.
        var from = D(2026, 1, 1);
        var to = D(2026, 12, 31);
        var sql = _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == PortfolioId &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                (p.PaymentType == PaymentType.Rent || p.PaymentType == PaymentType.LateFee) &&
                (p.PaidDate ?? p.DueDate) >= from && (p.PaidDate ?? p.DueDate) <= to)
            .GroupBy(p => new { (p.PaidDate ?? p.DueDate).Year, (p.PaidDate ?? p.DueDate).Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Total = g.Sum(p => p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount),
            })
            .ToQueryString();

        sql.Should().Contain("GROUP BY", "the monthly cash-flow aggregation must run in the database");
        // The SUM runs in SQL too. EF emits SUM( on Postgres and ef_sum( on SQLite (decimal-safe helper);
        // accept either so the assertion proves DB-side aggregation regardless of provider.
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the period total must be summed in the database");
    }

    [Fact]
    public async Task GetCashFlowAsync_TotalsAreSummedInSql()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1", property.Id), SeedTenant("Ann", "Acre"), rent: 1000m);

        SeedPayment(lease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedPayment(lease, 500m, dueDate: D(2026, 2, 1), PaymentStatus.Paid, paidDate: D(2026, 2, 5));
        SeedExpense(property.Id, 250m, paidAt: D(2026, 1, 20));

        _executedSql.Clear();

        var report = await _sut.GetCashFlowAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 2, 28),
            PropertyIds = [property.Id],
        }, CancellationToken.None);

        report.TotalIncome.Should().Be(1500m);
        report.TotalExpense.Should().Be(250m);
        report.TotalNet.Should().Be(1250m);

        var totalSql = _executedSql
            .Where(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
                          !sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
            .ToList();

        totalSql.Should().NotBeEmpty("cash-flow grand totals must be summed in SQL, not from month DTOs");
    }

    // ── Property P&L Summary (DB) ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPropertyProfitAndLossAsync_FiltersGroupsAndSumsInSql()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        var mapleLease = SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1000m);
        var oakLease = SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Bob", "Birch"), rent: 2000m);

        SeedPayment(mapleLease, 1000m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedPayment(mapleLease, 777m, dueDate: D(2026, 2, 1), PaymentStatus.Paid, paidDate: D(2026, 2, 5));
        SeedPayment(oakLease, 222m, dueDate: D(2026, 1, 1), PaymentStatus.Paid, paidDate: D(2026, 1, 5));
        SeedExpense(maple.Id, 300m, paidAt: D(2026, 1, 10));
        SeedExpense(maple.Id, 444m, paidAt: D(2026, 2, 10));
        SeedExpense(oak.Id, 111m, paidAt: D(2026, 1, 10));

        _executedSql.Clear();

        var report = await _sut.GetPropertyProfitAndLossAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
            PropertyIds = [maple.Id],
        }, CancellationToken.None);

        var row = report.Rows.Should().ContainSingle().Subject;
        row.PropertyId.Should().Be(maple.Id);
        row.Income.Should().Be(1000m);
        row.Expense.Should().Be(300m);
        row.Net.Should().Be(700m);
        report.TotalIncome.Should().Be(1000m);
        report.TotalExpense.Should().Be(300m);
        report.TotalNet.Should().Be(700m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("GROUP BY", "payment and expense totals must be grouped in SQL, not after materialization");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("payment and expense totals must be summed in SQL");

        _executedSql.Should().Contain(command =>
            command.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase),
            "property P&L grand totals must be summed in SQL, not from the property DTO dictionary");
    }

    // ── True cash flow (§9/§18, DB) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTrueCashFlowAsync_WorkedExample_EscrowNotDoubleCounted_NoDeposits()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);

        // Income: 1000 rent (paid) + 200 partial-collected + 50 late fee = 1250. Deposit excluded.
        SeedPayment(lease, 1000m, dueDate: D(2026, 3, 1), PaymentStatus.Paid, paidDate: D(2026, 3, 2));
        var partial = SeedPayment(lease, 1000m, dueDate: D(2026, 3, 10), PaymentStatus.Partial, paidDate: D(2026, 3, 10));
        partial.AmountPaid = 200m;
        SeedPayment(lease, 50m, dueDate: D(2026, 3, 12), PaymentStatus.Paid, paidDate: D(2026, 3, 12), type: PaymentType.LateFee);
        SeedPayment(lease, 1500m, dueDate: D(2026, 3, 1), PaymentStatus.Paid, paidDate: D(2026, 3, 1), type: PaymentType.SecurityDeposit);
        _db.SaveChanges();

        // Expenses: 300 repairs (counts) + 240 taxes (escrow-funded → EXCLUDED from cash-flow opex).
        SeedExpense(property.Id, 300m, paidAt: D(2026, 3, 15), category: ScheduleECategory.Repairs);
        SeedExpense(property.Id, 240m, paidAt: D(2026, 3, 18), category: ScheduleECategory.Taxes);

        // A loan that escrows taxes; one payment in the period: 800 P&I + 240 escrow = 1040 debt service.
        var loan = SeedLoan(property.Id, escrowCoversTaxes: true, monthlyEscrow: 240m);
        SeedLoanPayment(loan, "2026-03", dueDate: D(2026, 3, 1), interest: 500m, principal: 300m, escrow: 240m, total: 1040m, balanceAfter: 199_700m);

        _executedSql.Clear();

        var report = await _sut.GetTrueCashFlowAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 3, 1),
            To = D(2026, 3, 31),
        }, CancellationToken.None);

        report.Properties.Should().HaveCount(1);
        var p = report.Properties[0];
        p.Income.Should().Be(1250m);                 // rent + partial-collected + late fee, no deposit
        p.OperatingExpenses.Should().Be(300m);       // taxes excluded (escrow-funded) → only repairs
        p.Noi.Should().Be(950m);                     // 1250 − 300
        p.DebtService.Should().Be(1040m);            // full P&I + escrow
        p.CashFlow.Should().Be(-90m);                // 950 − 1040

        report.TotalCashFlow.Should().Be(-90m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("EXISTS", "escrow-funded operating-expense exclusion must run in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("true-cash-flow row values and totals must be summed in SQL");
    }

    [Fact]
    public async Task GetTrueCashFlowAsync_NonEscrowLoan_KeepsTaxesInOpex()
    {
        var property = SeedProperty("Maple");
        var lease = SeedLease(property, SeedUnit("1"), SeedTenant("Ann", "Acre"), rent: 1000m);
        SeedPayment(lease, 1000m, dueDate: D(2026, 3, 1), PaymentStatus.Paid, paidDate: D(2026, 3, 2));

        SeedExpense(property.Id, 240m, paidAt: D(2026, 3, 18), category: ScheduleECategory.Taxes);

        // Loan does NOT escrow taxes → taxes stay in operating expenses.
        var loan = SeedLoan(property.Id, escrowCoversTaxes: false, monthlyEscrow: 0m);
        SeedLoanPayment(loan, "2026-03", dueDate: D(2026, 3, 1), interest: 500m, principal: 300m, escrow: 0m, total: 800m, balanceAfter: 199_700m);

        var report = await _sut.GetTrueCashFlowAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 3, 1),
            To = D(2026, 3, 31),
        }, CancellationToken.None);

        var p = report.Properties[0];
        p.OperatingExpenses.Should().Be(240m);       // taxes kept (not escrow-funded)
        p.DebtService.Should().Be(800m);
        p.CashFlow.Should().Be(1000m - 240m - 800m); // -40
    }

    // ── Year-end view (§11/§18, DB) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetYearEndAsync_ReturnsThreeBlocks_CashFlowDistinctFromTaxableIncome()
    {
        var property = SeedProperty("Maple");
        // Depreciation basis → a non-cash deduction that makes taxable income differ from cash flow.
        property.PurchasePrice = 300_000m;
        property.LandValue = 60_000m;
        property.InServiceDate = D(2020, 1, 1);
        var unit = SeedUnit("1", property.Id, UnitStatus.Occupied);
        var lease = SeedLease(property, unit, SeedTenant("Ann", "Acre"), rent: 1_000m,
            start: D(2025, 1, 1), end: D(2026, 1, 1), status: LeaseStatus.Active);
        _db.SaveChanges();

        // 12,000 rent collected in 2025.
        for (var m = 1; m <= 12; m++)
            SeedPayment(lease, 1_000m, dueDate: D(2025, m, 1), PaymentStatus.Paid, paidDate: D(2025, m, 1));

        // A loan: one payment → debt service 700 (interest 500 + principal 200).
        var loan = SeedLoan(property.Id, escrowCoversTaxes: false);
        SeedLoanPayment(loan, "2025-01", dueDate: D(2025, 1, 1), interest: 500m, principal: 200m, escrow: 0m, total: 700m, balanceAfter: 199_800m);

        var view = await _sut.GetYearEndAsync(PortfolioId, 2025, ct: CancellationToken.None);

        view.Year.Should().Be(2025);

        // Block 1: cash flow present (income 12,000, debt service 700, no depreciation).
        view.CashFlow.Properties.Should().ContainSingle();
        view.CashFlow.Properties[0].Income.Should().Be(12_000m);
        view.CashFlow.Properties[0].DebtService.Should().Be(700m);
        var cashFlow = view.CashFlow.TotalCashFlow;

        // Block 2: taxable income present, with depreciation (8,727.27) + interest (500), principal out.
        view.ScheduleE.Properties.Should().ContainSingle();
        view.ScheduleE.Properties[0].Depreciation.Should().Be(8_727.27m);
        view.ScheduleE.Properties[0].MortgageInterest.Should().Be(500m);
        var taxableIncome = view.ScheduleE.NetIncome;

        // The two key numbers are DISTINCT (depreciation + the cash/tax divide separate them).
        cashFlow.Should().NotBe(taxableIncome);

        // Block 3: rent roll present.
        view.RentRoll.Should().ContainSingle();
        view.RentRoll[0].MonthlyRent.Should().Be(1_000m);

        // §18 caveats are surfaced (always-on disposition + mixed-use notes at minimum).
        view.AccountantNotes.Should().NotBeEmpty();
        view.AccountantNotes.Should().Contain(n => n.Contains("recapture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetYearEndAsync_PropertyFilter_ReturnsOnlyThatProperty()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        var mapleLease = SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1_000m,
            start: D(2025, 1, 1), end: D(2026, 1, 1), status: LeaseStatus.Active);
        var oakLease = SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Bob", "Birch"), rent: 2_000m,
            start: D(2025, 1, 1), end: D(2026, 1, 1), status: LeaseStatus.Active);

        SeedPayment(mapleLease, 1_000m, dueDate: D(2025, 1, 1), PaymentStatus.Paid, paidDate: D(2025, 1, 5));
        SeedPayment(oakLease, 2_000m, dueDate: D(2025, 1, 1), PaymentStatus.Paid, paidDate: D(2025, 1, 5));
        SeedExpense(maple.Id, 250m, paidAt: D(2025, 1, 10));
        SeedExpense(oak.Id, 700m, paidAt: D(2025, 1, 10));

        var view = await _sut.GetYearEndAsync(PortfolioId, 2025, maple.Id, CancellationToken.None);

        view.CashFlow.Properties.Should().ContainSingle(p => p.PropertyId == maple.Id);
        view.CashFlow.TotalIncome.Should().Be(1_000m);
        view.CashFlow.TotalOperatingExpenses.Should().Be(250m);

        view.ScheduleE.Properties.Should().ContainSingle(p => p.PropertyId == maple.Id);
        view.ScheduleE.TotalRentalIncome.Should().Be(1_000m);
        view.ScheduleE.TotalExpenses.Should().Be(250m);

        view.RentRoll.Should().ContainSingle(r => r.PropertyName == "Maple");
        view.RentRoll.Should().NotContain(r => r.PropertyName == "Oak");
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

        _executedSql.Clear();

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

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("COUNT", "occupancy row counts must be computed in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("portfolio occupancy totals must be summed in SQL");
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
            DeductionsTotal = 200m,
            CreatedAt = D(2025, 1, 1),
            UpdatedAt = D(2026, 1, 1),
        });
        _db.SaveChanges();

        _executedSql.Clear();

        var report = await _sut.GetSecurityDepositRegisterAsync(PortfolioId, new ReportRangeQuery(), CancellationToken.None);

        var row = report.Rows.Should().ContainSingle().Subject;
        row.Held.Should().Be(1500m);
        row.Deductions.Should().Be(200m);
        row.Returned.Should().Be(300m);
        row.CurrentBalance.Should().Be(1000m);

        report.TotalHeld.Should().Be(1500m);
        report.TotalDeductions.Should().Be(200m);
        report.TotalReturned.Should().Be(300m);
        report.TotalCurrentBalance.Should().Be(1000m);

        var totalSql = _executedSql
            .Where(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase))
            .ToList();

        totalSql.Should().NotBeEmpty("security deposit register totals must be summed in SQL, not from materialized rows");
        string.Join("\n---\n", totalSql).Should().Contain("DeductionsTotal");
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

    [Fact]
    public async Task RentRoll_ComputesTotalsInSql()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");
        SeedLease(maple, SeedUnit("1", maple.Id), SeedTenant("Ann", "Acre"), rent: 1000m);
        SeedLease(oak, SeedUnit("A", oak.Id), SeedTenant("Bob", "Birch"), rent: 2000m);

        _executedSql.Clear();

        var report = await _sut.GetRentRollAsync(PortfolioId, new ReportRangeQuery(), CancellationToken.None);

        report.LeaseCount.Should().Be(2);
        report.TotalMonthlyRent.Should().Be(3000m);
        report.TotalSecurityDeposit.Should().Be(3000m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("COUNT", "rent-roll lease count must be aggregated in SQL, not from materialized rows");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("rent-roll money totals must be summed in SQL");
    }

    [Fact]
    public async Task Vendor1099_FiltersAndTotalsInSql()
    {
        var paidEligible = SeedVendor("Clearline Plumbing", eligible: true, w9OnFile: false);
        var paidIneligible = SeedVendor("Oak City Repairs", eligible: false, w9OnFile: false);
        var eligibleNoPayments = SeedVendor("KeyPro Locksmith", eligible: true, w9OnFile: true);
        SeedVendor("No Activity Cleaning", eligible: false, w9OnFile: false);

        SeedVendorExpense(paidEligible, 700m, D(2026, 2, 1));
        SeedVendorExpense(paidEligible, 99m, D(2025, 12, 31));
        SeedVendorExpense(paidIneligible, 250m, D(2026, 3, 1));
        _db.SaveChanges();
        _executedSql.Clear();

        var report = await _sut.GetVendor1099Async(PortfolioId, 2026, CancellationToken.None);

        report.Rows.Select(r => r.VendorName).Should().Equal(
            "Clearline Plumbing",
            "KeyPro Locksmith",
            "Oak City Repairs");
        report.Rows.Single(r => r.VendorName == paidEligible.Name).TotalPaid.Should().Be(700m);
        report.Rows.Single(r => r.VendorName == paidEligible.Name).Needs1099Review.Should().BeTrue();
        report.Rows.Single(r => r.VendorName == eligibleNoPayments.Name).TotalPaid.Should().Be(0m);
        report.Rows.Single(r => r.VendorName == paidIneligible.Name).TotalPaid.Should().Be(250m);
        report.TotalPaid.Should().Be(950m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("EXISTS", "vendors with paid expenses should be filtered in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("1099 totals must be summed in SQL");
        sql.Should().NotContain("strftime", "the tax-year filter should be a date range, not a client/year extraction filter");
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

        _executedSql.Clear();

        var report = await _sut.GetLeaseExpirationsAsync(PortfolioId, new ReportRangeQuery(), days: 90, CancellationToken.None);

        report.WindowDays.Should().Be(90);
        report.Rows.Should().ContainSingle();
        report.Rows[0].LeaseId.Should().Be(soon.Id);
        report.Rows[0].DaysUntilExpiry.Should().BeInRange(19, 21);
        report.TotalMonthlyRent.Should().Be(1000m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("COUNT", "lease-expiration totals must be counted in SQL, not from materialized rows");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("lease-expiration monthly rent total must be summed in SQL");
    }

    [Fact]
    public async Task GetWorkOrdersAsync_CountsAndSumsInSql()
    {
        var maple = SeedProperty("Maple");
        var oak = SeedProperty("Oak");

        SeedWorkOrder(maple, "Leaking sink", D(2026, 1, 5), WorkOrderStatus.New, actualCost: 125.50m);
        SeedWorkOrder(maple, "Replace lock", D(2026, 1, 10), WorkOrderStatus.Completed, actualCost: 300m);
        SeedWorkOrder(oak, "Oak repair", D(2026, 1, 8), WorkOrderStatus.Completed, actualCost: 999m);
        SeedWorkOrder(maple, "Old request", D(2025, 12, 25), WorkOrderStatus.InProgress, actualCost: 50m);

        _executedSql.Clear();

        var report = await _sut.GetWorkOrdersAsync(PortfolioId, new ReportRangeQuery
        {
            From = D(2026, 1, 1),
            To = D(2026, 1, 31),
            PropertyIds = [maple.Id],
        }, CancellationToken.None);

        report.Rows.Should().HaveCount(2);
        report.TotalCount.Should().Be(2);
        report.OpenCount.Should().Be(1);
        report.CompletedCount.Should().Be(1);
        report.TotalActualCost.Should().Be(425.50m);

        var sql = string.Join("\n---\n", _executedSql);
        sql.Should().Contain("ORDER BY", "work-order row sorting must run in SQL");
        sql.Should().Contain("COUNT", "summary counts must be computed in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("actual-cost rollup must be summed in SQL");
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

    private Expense SeedExpense(int propertyId, decimal amount, DateTime paidAt,
        ScheduleECategory category = ScheduleECategory.Repairs)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Category = category,
            Description = category.ToString(),
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

    private Vendor SeedVendor(string name, bool eligible, bool w9OnFile)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = name,
            ServiceType = "Repairs",
            Is1099Eligible = eligible,
            W9OnFile = w9OnFile,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Vendors.Add(vendor);
        return vendor;
    }

    private Expense SeedVendorExpense(Vendor vendor, decimal amount, DateTime paidAt)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Vendor = vendor,
            Category = ScheduleECategory.Repairs,
            Description = $"{vendor.Name} paid expense",
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = paidAt,
            PaidAt = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _db.Expenses.Add(expense);
        return expense;
    }

    private WorkOrder SeedWorkOrder(
        Property property,
        string title,
        DateTime requestedAt,
        WorkOrderStatus status,
        decimal? actualCost = null)
    {
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            Title = title,
            Description = title,
            Category = "General",
            Priority = WorkOrderPriority.Normal,
            Status = status,
            RequestedAt = requestedAt,
            CompletedAt = status == WorkOrderStatus.Completed ? requestedAt.AddDays(1) : null,
            ActualCost = actualCost,
            UpdatedAt = requestedAt,
        };
        _db.WorkOrders.Add(workOrder);
        _db.SaveChanges();
        return workOrder;
    }

    private Loan SeedLoan(int propertyId, bool escrowCoversTaxes = false, bool escrowCoversInsurance = false,
        decimal monthlyEscrow = 0m, LoanStatus status = LoanStatus.Active)
    {
        var now = DateTime.UtcNow;
        var loan = new Loan
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Lender = "Test Bank",
            OriginalAmount = 200_000m,
            CurrentBalance = 200_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = D(2026, 1, 1),
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 800m,
            MonthlyEscrow = monthlyEscrow,
            EscrowCoversTaxes = escrowCoversTaxes,
            EscrowCoversInsurance = escrowCoversInsurance,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Loans.Add(loan);
        _db.SaveChanges();
        return loan;
    }

    private LoanPayment SeedLoanPayment(Loan loan, string periodKey, DateTime dueDate,
        decimal interest, decimal principal, decimal escrow, decimal total, decimal balanceAfter)
    {
        var payment = new LoanPayment
        {
            PortfolioId = PortfolioId,
            LoanId = loan.Id,
            PeriodKey = periodKey,
            DueDate = dueDate,
            InterestAmount = interest,
            PrincipalAmount = principal,
            EscrowAmount = escrow,
            TotalAmount = total,
            BalanceAfter = balanceAfter,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
        };
        _db.LoanPayments.Add(payment);
        _db.SaveChanges();
        return payment;
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            commands.Add(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
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
