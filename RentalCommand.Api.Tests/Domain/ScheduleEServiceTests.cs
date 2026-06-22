using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Schedule E is the tax return: these tests pin spec §10 with the §18 corrections — taxable income
/// uses actual cash received (no deposits), deducts mortgage INTEREST (from the loan split, principal
/// excluded) and computed DEPRECIATION, and applies the deterministic legacy double-count exclusion
/// (a loan present → drop manual MortgageInterest; computed depreciation → drop manual Depreciation).
/// </summary>
public class ScheduleEServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int Year = 2025;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly ScheduleEService _sut;

    public ScheduleEServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new ScheduleERecordingCommandInterceptor(_commands))
            .Options;

        _db = new ReportsServiceTestDbContext(options); // reuses the SQLite-compatible context
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Frank's Rentals",
            ManagementCompanyName = "Frank Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new ScheduleEService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetReportAsync_DeductsInterestAndDepreciation_ExcludesPrincipalAndDeposits_NoDoubleCount()
    {
        // Property with a depreciation basis: building 240k → full-year 8,727.27 (in service 2020).
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple",
            AddressLine1 = "1 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            PurchasePrice = 300_000m,
            LandValue = 60_000m,
            InServiceDate = D(2020, 1, 1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit { PropertyId = property.Id, UnitNumber = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var tenant = new Tenant { PortfolioId = PortfolioId, FirstName = "Ann", LastName = "Acre", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Units.Add(unit);
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-1",
            Status = LeaseStatus.Active,
            StartDate = D(2025, 1, 1),
            EndDate = D(2026, 1, 1),
            MonthlyRent = 1_000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();

        // Income: 12,000 rent (paid) + a 1,500 deposit (EXCLUDED).
        for (var m = 1; m <= 12; m++)
            _db.Payments.Add(new Payment
            {
                PortfolioId = PortfolioId, LeaseId = lease.Id, PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Paid, Amount = 1_000m, DueDate = D(Year, m, 1), PaidDate = D(Year, m, 1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId, LeaseId = lease.Id, PaymentType = PaymentType.SecurityDeposit,
            Status = PaymentStatus.Paid, Amount = 1_500m, DueDate = D(Year, 1, 1), PaidDate = D(Year, 1, 1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        // Expenses: 1,000 repairs (counts) + a 5,000 manual MortgageInterest + 3,000 manual Depreciation
        // — both must be EXCLUDED because the property has a modeled loan + computed depreciation.
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, PropertyId = property.Id, Category = ScheduleECategory.Repairs, Description = "Repair", Status = ExpenseStatus.Paid, Amount = 1_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, PropertyId = property.Id, Category = ScheduleECategory.MortgageInterest, Description = "Manual interest", Status = ExpenseStatus.Paid, Amount = 5_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Expenses.Add(new Expense { PortfolioId = PortfolioId, PropertyId = property.Id, Category = ScheduleECategory.Depreciation, Description = "Manual depr", Status = ExpenseStatus.Paid, Amount = 3_000m, IncurredAt = D(Year, 6, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        // A loan with 2 payments this year: interest 600 + 590 = 1,190 deducted; principal NOT deducted.
        var loan = new Loan
        {
            PortfolioId = PortfolioId, PropertyId = property.Id, Lender = "Bank",
            OriginalAmount = 100_000m, CurrentBalance = 100_000m, AnnualInterestRatePct = 6m,
            TermMonths = 360, StartDate = D(Year, 1, 1), DayOfMonthDue = 1, MonthlyPrincipalInterest = 600m,
            Status = LoanStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Loans.Add(loan);
        _db.SaveChanges();
        _db.LoanPayments.Add(new LoanPayment { PortfolioId = PortfolioId, LoanId = loan.Id, PeriodKey = $"{Year}-01", DueDate = D(Year, 1, 1), InterestAmount = 600m, PrincipalAmount = 100m, EscrowAmount = 0m, TotalAmount = 700m, BalanceAfter = 99_900m, Status = LoanPaymentStatus.Scheduled, CreatedAt = DateTime.UtcNow });
        _db.LoanPayments.Add(new LoanPayment { PortfolioId = PortfolioId, LoanId = loan.Id, PeriodKey = $"{Year}-02", DueDate = D(Year, 2, 1), InterestAmount = 590m, PrincipalAmount = 110m, EscrowAmount = 0m, TotalAmount = 700m, BalanceAfter = 99_790m, Status = LoanPaymentStatus.Scheduled, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        var report = await _sut.GetReportAsync(PortfolioId, Year, ct: CancellationToken.None);

        report.Properties.Should().HaveCount(1);
        var p = report.Properties[0];

        p.RentalIncome.Should().Be(12_000m, "deposits are excluded from taxable income");
        p.MortgageInterest.Should().Be(1_190m, "interest comes from the loan split (principal excluded)");
        p.Depreciation.Should().Be(8_727.27m, "depreciation is computed from basis (240k / 27.5)");

        // Deductions = 1,000 repairs + 1,190 interest + 8,727.27 depreciation = 10,917.27.
        // The manual 5,000 interest + 3,000 depreciation are EXCLUDED (no double-count).
        p.TotalExpenses.Should().Be(10_917.27m);
        p.NetIncome.Should().Be(12_000m - 10_917.27m);

        // The category breakdown carries the modeled interest + depreciation, not the manual ones.
        p.ExpensesByCategory.Should().Contain(c => c.Category == "MortgageInterest" && c.Amount == 1_190m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Depreciation" && c.Amount == 8_727.27m);
        p.ExpensesByCategory.Should().Contain(c => c.Category == "Repairs" && c.Amount == 1_000m);
        // Exactly one MortgageInterest + one Depreciation line (the manual ones were dropped).
        p.ExpensesByCategory.Count(c => c.Category == "MortgageInterest").Should().Be(1);
        p.ExpensesByCategory.Count(c => c.Category == "Depreciation").Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_PartialRent_UsesAmountPaid()
    {
        var property = new Property { PortfolioId = PortfolioId, Name = "Oak", AddressLine1 = "2 Oak", City = "C", State = "OH", PostalCode = "43215", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Properties.Add(property);
        _db.SaveChanges();
        var unit = new Unit { PropertyId = property.Id, UnitNumber = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var tenant = new Tenant { PortfolioId = PortfolioId, FirstName = "Bo", LastName = "Birch", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Units.Add(unit); _db.Tenants.Add(tenant); _db.SaveChanges();
        var lease = new Lease { PortfolioId = PortfolioId, PropertyId = property.Id, UnitId = unit.Id, TenantId = tenant.Id, LeaseNumber = "L-2", Status = LeaseStatus.Active, StartDate = D(2025, 1, 1), EndDate = D(2026, 1, 1), MonthlyRent = 1_000m, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Leases.Add(lease); _db.SaveChanges();

        // 1,000 paid + a partial paying 250 of 1,000 → taxable income 1,250 (not 2,000).
        _db.Payments.Add(new Payment { PortfolioId = PortfolioId, LeaseId = lease.Id, PaymentType = PaymentType.Rent, Status = PaymentStatus.Paid, Amount = 1_000m, DueDate = D(Year, 1, 1), PaidDate = D(Year, 1, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.Payments.Add(new Payment { PortfolioId = PortfolioId, LeaseId = lease.Id, PaymentType = PaymentType.Rent, Status = PaymentStatus.Partial, Amount = 1_000m, AmountPaid = 250m, DueDate = D(Year, 2, 1), PaidDate = D(Year, 2, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();

        _commands.Clear();

        var report = await _sut.GetReportAsync(PortfolioId, Year, ct: CancellationToken.None);
        report.Properties.Single().RentalIncome.Should().Be(1_250m);

        var sql = string.Join("\n---\n", _commands);
        sql.Should().Contain("GROUP BY", "Schedule E income and category totals must be grouped in SQL");
        (sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) || sql.Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("Schedule E raw money totals must be summed in SQL");

        var orderedPropertySql = _commands.FirstOrDefault(command =>
            command.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
        orderedPropertySql.Should().NotBeNull("Schedule E property ordering must come from SQL before DTO shaping");
    }
}

internal sealed class ScheduleERecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
