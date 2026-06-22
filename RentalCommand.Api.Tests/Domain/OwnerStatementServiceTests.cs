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
/// Pins the round-3 DB-side rewrite of OwnerStatementService (L-10): the per-property rental-income
/// and expense totals are grouped + summed in SQL, not by materializing payment/expense rows and
/// grouping in memory. Runs against the real (SQLite) query engine so a GroupBy that fails to
/// translate — or that drifts from the in-memory values — is caught.
/// </summary>
public class OwnerStatementServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int Year = 2026;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly OwnerStatementService _sut;

    public OwnerStatementServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new OwnerStatementRecordingCommandInterceptor(_commands))
            .Options;

        _db = new OwnerStatementTestDbContext(options);
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

        _sut = new OwnerStatementService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetForOwnerAsync_SumsIncomeAndExpensesPerPropertyDbSide()
    {
        var owner = SeedOwner("Acme Holdings");

        // Property A: 10% mgmt fee. Two paid rent payments in-year ($1,200 + $1,200) and one expense
        // ($300). A third payment is NOT Paid and must be excluded from income.
        var propA = SeedProperty(owner.Id, "Maple Duplex", managementFeePercent: 10m);
        var leaseA = SeedLease(propA, "L-A");
        SeedRent(leaseA, 1200m, paidInYear: true);
        SeedRent(leaseA, 1200m, paidInYear: true);
        SeedRent(leaseA, 1200m, paidInYear: false); // scheduled, not collected → excluded
        SeedExpense(propA.Id, 300m);

        // Property B: 0% mgmt fee. One paid rent ($900) and two expenses ($100 + $50).
        var propB = SeedProperty(owner.Id, "Oak Cottage", managementFeePercent: 0m);
        var leaseB = SeedLease(propB, "L-B");
        SeedRent(leaseB, 900m, paidInYear: true);
        SeedExpense(propB.Id, 100m);
        SeedExpense(propB.Id, 50m);

        // A paid rent in a DIFFERENT year must be excluded entirely.
        SeedRent(leaseA, 5000m, paidInYear: true, year: Year - 1);

        _commands.Clear();

        var report = await _sut.GetForOwnerAsync(PortfolioId, owner.Id, Year, CancellationToken.None);

        report.Should().NotBeNull();
        report!.Properties.Should().HaveCount(2);

        var lineA = report.Properties.Single(p => p.PropertyName == "Maple Duplex");
        lineA.RentalIncome.Should().Be(2400m);          // 1200 + 1200, scheduled + prior-year excluded
        lineA.Expenses.Should().Be(300m);
        lineA.ManagementFee.Should().Be(240m);          // 10% of 2400
        lineA.NetToOwner.Should().Be(1860m);            // 2400 - 300 - 240

        var lineB = report.Properties.Single(p => p.PropertyName == "Oak Cottage");
        lineB.RentalIncome.Should().Be(900m);
        lineB.Expenses.Should().Be(150m);               // 100 + 50
        lineB.ManagementFee.Should().Be(0m);
        lineB.NetToOwner.Should().Be(750m);             // 900 - 150 - 0

        report.TotalIncome.Should().Be(3300m);          // 2400 + 900
        report.TotalExpenses.Should().Be(450m);         // 300 + 150
        report.TotalManagementFee.Should().Be(240m);
        report.TotalNetToOwner.Should().Be(2610m);      // 1860 + 750

        var propertyLineSql = _commands.FirstOrDefault(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        propertyLineSql.Should().NotBeNull("owner-statement property lines must be filtered, ordered, and aggregated in SQL");
    }

    [Fact]
    public async Task ListOwnersWithNetAsync_ComputesPerOwnerNetDbSide()
    {
        var owner1 = SeedOwner("Acme Holdings");
        var owner2 = SeedOwner("Beta Estates");

        var p1 = SeedProperty(owner1.Id, "Maple Duplex", managementFeePercent: 10m);
        SeedRent(SeedLease(p1, "L-1"), 2000m, paidInYear: true);
        SeedExpense(p1.Id, 500m);

        var p2 = SeedProperty(owner2.Id, "Oak Cottage", managementFeePercent: 0m);
        SeedRent(SeedLease(p2, "L-2"), 1000m, paidInYear: true);
        SeedExpense(p2.Id, 250m);

        _commands.Clear();

        var summaries = await _sut.ListOwnersWithNetAsync(PortfolioId, Year, CancellationToken.None);

        summaries.Should().HaveCount(2);
        // Owner1: 2000 income - 500 expenses - 200 mgmt (10%) = 1300.
        summaries.Single(s => s.OwnerName == "Acme Holdings").NetToOwner.Should().Be(1300m);
        // Owner2: 1000 income - 250 expenses - 0 mgmt = 750.
        summaries.Single(s => s.OwnerName == "Beta Estates").NetToOwner.Should().Be(750m);

        var ownerSummarySql = _commands.FirstOrDefault(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        ownerSummarySql.Should().NotBeNull("owner net summaries must group and sum per owner in SQL");
    }

    // ── seed helpers ───────────────────────────────────────────────────────────────────────────
    private OwnerEntity SeedOwner(string name)
    {
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.OwnerEntities.Add(owner);
        _db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(int ownerId, string name, decimal managementFeePercent)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerId,
            Name = name,
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            ManagementFeePercent = managementFeePercent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private Lease SeedLease(Property property, string leaseNumber)
    {
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            MarketRent = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Pat",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = leaseNumber,
            Status = LeaseStatus.Active,
            StartDate = new DateTime(Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

    private void SeedRent(Lease lease, decimal amount, bool paidInYear, int? year = null)
    {
        var y = year ?? Year;
        var paidDate = new DateTime(y, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = paidInYear ? PaymentStatus.Paid : PaymentStatus.Scheduled,
            Amount = amount,
            DueDate = paidDate,
            PaidDate = paidInYear ? paidDate : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void SeedExpense(int propertyId, decimal amount)
    {
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Description = "Repair",
            Category = ScheduleECategory.Repairs,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = new DateTime(Year, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }
}

internal sealed class OwnerStatementTestDbContext : RentalCommandDbContext
{
    public OwnerStatementTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Map Postgres jsonb columns to TEXT so EnsureCreated works on SQLite.
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Payment>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().ToTable("Leases");
        modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
    }
}

internal sealed class OwnerStatementRecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
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
