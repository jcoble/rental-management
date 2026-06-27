using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Pins the enriched dashboard "Recent Activity" feed: every row must carry the touched entity's
/// <c>EntityId</c> (for deep-linking) and a human <c>Label</c> naming the specific record, and the
/// labels must be resolved with ONE batched, portfolio-scoped query per entity type — never a
/// per-row lookup (the hard data-access rule).
/// </summary>
public class DashboardRecentActivityTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly DashboardService _sut;

    public DashboardRecentActivityTests()
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

        _sut = new DashboardService(_db, new AuditDescriber());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RecentActivity_NamesEachEntityAndCarriesEntityId()
    {
        var seeded = SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId);

        dashboard.Should().NotBeNull();
        var byKey = dashboard!.RecentActivity.ToDictionary(r => (r.Type, r.EntityId));

        byKey[("Tenant", seeded.Tenant1.Id)].Label.Should().Be("Maria Tenant");
        byKey[("Tenant", seeded.Tenant2.Id)].Label.Should().Be("Liam Renter");
        byKey[("Tenant", seeded.Tenant3.Id)].Label.Should().Be("Noah Lessee");
        byKey[("Unit", seeded.Unit.Id)].Label.Should().Be("Maple · Unit 1A");
        byKey[("Lease", seeded.Lease.Id)].Label.Should().Be("L-1A");
        byKey[("WorkOrder", seeded.WorkOrder.Id)].Label.Should().Be("Fix sink");
        byKey[("Property", seeded.Property.Id)].Label.Should().Be("Maple");
        byKey[("Payment", seeded.Payment.Id)].Label.Should().Be("Rent · $1,200.00");
        byKey[("Expense", seeded.Expense.Id)].Label.Should().Be("Plumbing parts");

        // Every row still carries the touched entity's id so the web can deep-link to it.
        dashboard.RecentActivity.Should().OnlyContain(r => r.EntityId > 0);
    }

    [Fact]
    public async Task RecentActivity_LeavesLabelNullForTypesWithoutACheapLabel()
    {
        SeedActivityGraph();

        var dashboard = await _sut.GetDashboardAsync(PortfolioId);

        dashboard.Should().NotBeNull();
        // The unresolved "Conversation" row keeps its id but gets no label (web shows verb-only).
        var unresolved = dashboard!.RecentActivity.Single(r => r.Type == "Conversation");
        unresolved.EntityId.Should().Be(999);
        unresolved.Label.Should().BeNull();
    }

    [Fact]
    public async Task RecentActivity_ResolvesLabelsWithOneBatchedQueryPerType_NotPerRow()
    {
        // Three tenant audit rows in the feed must resolve through a SINGLE Tenants query, not three.
        SeedActivityGraph();

        _executedSql.Clear();
        await _sut.GetDashboardAsync(PortfolioId);

        // The label lookup is the portfolio-scoped Tenants query whose ids arrive as one IN set. A
        // per-row resolver would emit three such queries (or three single-id equalities); a batched one
        // emits exactly one. (The expiring-lease query also touches Tenants, but via a JOIN subquery
        // without an IN/PortfolioId filter, so it is excluded here.)
        var tenantLabelQueries = _executedSql
            .Where(c => c.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase)
                && c.Contains(" IN (", StringComparison.OrdinalIgnoreCase)
                && c.Contains("\"PortfolioId\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        tenantLabelQueries.Should().HaveCount(1,
            "the three tenant rows must be labelled by one set-based IN-query, not one query per row");
    }

    private SeededActivityGraph SeedActivityGraph()
    {
        var baseTime = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1200m,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var tenant1 = Tenant("Maria", "Tenant", baseTime);
        var tenant2 = Tenant("Liam", "Renter", baseTime);
        var tenant3 = Tenant("Noah", "Lessee", baseTime);
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant1,
            LeaseNumber = "L-1A",
            Status = LeaseStatus.Active,
            StartDate = baseTime.AddMonths(-1),
            EndDate = baseTime.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Fix sink",
            Description = "Leak under the kitchen sink",
            RequestedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = baseTime.Date,
            PaidDate = baseTime.Date,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Description = "Plumbing parts",
            Amount = 40m,
            IncurredAt = baseTime,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };

        _db.AddRange(property, unit, tenant1, tenant2, tenant3, lease, workOrder, payment, expense);
        _db.SaveChanges();

        // Nine audit rows (<= the Take(10) cap), newest first by timestamp. One row references an
        // entity type the resolver does not label ("Conversation") to exercise the verb-only fallback.
        _db.AuditLogs.AddRange(
            Audit("Tenant", tenant1.Id, AuditLogOperation.Created, baseTime, 1),
            Audit("Tenant", tenant2.Id, AuditLogOperation.Updated, baseTime, 2),
            Audit("Tenant", tenant3.Id, AuditLogOperation.Created, baseTime, 3),
            Audit("Unit", unit.Id, AuditLogOperation.Updated, baseTime, 4),
            Audit("Lease", lease.Id, AuditLogOperation.Created, baseTime, 5),
            Audit("WorkOrder", workOrder.Id, AuditLogOperation.Created, baseTime, 6),
            Audit("Property", property.Id, AuditLogOperation.Updated, baseTime, 7),
            Audit("Payment", payment.Id, AuditLogOperation.Created, baseTime, 8),
            Audit("Expense", expense.Id, AuditLogOperation.Updated, baseTime, 9),
            Audit("Conversation", 999, AuditLogOperation.Created, baseTime, 10));
        _db.SaveChanges();

        return new SeededActivityGraph(property, unit, tenant1, tenant2, tenant3, lease, workOrder, payment, expense);
    }

    private Tenant Tenant(string first, string last, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        FirstName = first,
        LastName = last,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static AuditLog Audit(string entityType, int entityId, AuditLogOperation op, DateTime baseTime, int minutesAgo)
        => new()
        {
            PortfolioId = PortfolioId,
            EntityType = entityType,
            EntityId = entityId,
            Operation = op,
            ActorLabel = "test",
            Timestamp = baseTime.AddMinutes(-minutesAgo),
        };

    private sealed record SeededActivityGraph(
        Property Property,
        Unit Unit,
        Tenant Tenant1,
        Tenant Tenant2,
        Tenant Tenant3,
        Lease Lease,
        WorkOrder WorkOrder,
        Payment Payment,
        Expense Expense);
}
