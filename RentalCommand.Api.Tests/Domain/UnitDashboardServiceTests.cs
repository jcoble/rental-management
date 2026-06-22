using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class UnitDashboardServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly UnitDashboardService _sut;

    public UnitDashboardServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
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

        _sut = new UnitDashboardService(_db, new AuditDescriber(), new AuditDiffBuilder());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task GetTimelineAsync_ScopesChildAuditRowsInSqlWithoutPreloadingIds()
    {
        var seeded = SeedUnitWithTimelineChildren();
        _db.AuditLogs.AddRange(
            Audit("Unit", seeded.Unit.Id, 6),
            Audit("Lease", seeded.Lease.Id, 5),
            Audit("Payment", seeded.Payment.Id, 4),
            Audit("WorkOrder", seeded.WorkOrder.Id, 3),
            Audit("Expense", seeded.Expense.Id, 2),
            Audit("Lease", seeded.ForeignLease.Id, 1));
        _db.SaveChanges();

        _executedSql.Clear();

        var rows = await _sut.GetTimelineAsync(PortfolioId, seeded.Unit.Id, skip: 0, take: 10, ct: CancellationToken.None);

        rows.Select(r => r.EntityType).Should().BeEquivalentTo(["Unit", "Lease", "Payment", "WorkOrder", "Expense"]);
        rows.Should().NotContain(r => r.EntityId == seeded.ForeignLease.Id);

        _executedSql.Should().NotContain(command => IsChildIdPreload(command),
            "unit timeline child scoping must stay inside the paged AuditLogs query instead of materializing child id lists");

        var auditSql = _executedSql.Single(command => command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase));
        auditSql.Should().Contain("ORDER BY", "timeline sorting must be DB-side");
        auditSql.Should().Contain("LIMIT", "timeline paging must be DB-side");
        auditSql.Should().Contain("Leases", "lease child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("Payments", "payment child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("WorkOrders", "work-order child scope should be translated as a SQL subquery");
        auditSql.Should().Contain("Expenses", "expense child scope should be translated as a SQL subquery");
    }

    private static bool IsChildIdPreload(string command)
        => IsBareIdSelect(command, "Leases")
            || IsBareIdSelect(command, "Payments")
            || IsBareIdSelect(command, "WorkOrders")
            || IsBareIdSelect(command, "Inspections")
            || IsBareIdSelect(command, "Appointments")
            || IsBareIdSelect(command, "Expenses");

    private static bool IsBareIdSelect(string command, string table)
        => command.Contains($"SELECT \"", StringComparison.OrdinalIgnoreCase)
            && command.Contains($"\".\"Id\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains($"FROM \"{table}\"", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("FROM \"AuditLogs\"", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase);

    private AuditLog Audit(string entityType, int entityId, int minutesAgo)
        => new()
        {
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = AuditLogOperation.Created,
            ActorLabel = "test",
            Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo),
        };

    private SeededTimelineGraph SeedUnitWithTimelineChildren()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple",
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
            UnitNumber = "1A",
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
            LeaseNumber = "L-1A",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = now.Date,
            PaidDate = now.Date,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Fix sink",
            Description = "Leak",
            RequestedAt = now,
            UpdatedAt = now,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            WorkOrder = workOrder,
            Description = "Parts",
            Amount = 40m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var foreignUnit = new Unit
        {
            Property = property,
            UnitNumber = "9Z",
            MarketRent = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var foreignLease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = foreignUnit,
            Tenant = tenant,
            LeaseNumber = "L-9Z",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 900m,
            SecurityDeposit = 900m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.AddRange(property, unit, tenant, lease, payment, workOrder, expense, foreignUnit, foreignLease);
        _db.SaveChanges();

        return new SeededTimelineGraph(unit, lease, payment, workOrder, expense, foreignLease);
    }

    private sealed record SeededTimelineGraph(
        Unit Unit,
        Lease Lease,
        Payment Payment,
        WorkOrder WorkOrder,
        Expense Expense,
        Lease ForeignLease);
}
