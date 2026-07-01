using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class DailyBriefingServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly DailyBriefingService _sut;

    public DailyBriefingServiceTests()
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

        _sut = new DailyBriefingService(_db, new NoopLlmProvider(), TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ComposeAsync_RanksAndCapsBriefingCandidatesInSql()
    {
        SeedBriefingData();
        _executedSql.Clear();

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        briefing.Bullets.Should().Contain(b => b.Category == "Maintenance" && b.Severity == "critical");
        briefing.Bullets.Should().Contain(b => b.Category == "RentLate");
        briefing.Bullets.Should().Contain(b => b.Category == "RentDue");
        briefing.Bullets.Should().Contain(b => b.Category == "Appointment");
        briefing.Bullets.Should().Contain(b => b.Category == "Inspection");
        briefing.Bullets.Should().Contain(b => b.Category == "LeaseExpiring");
        briefing.Bullets
            .Where(b => b.EntityType is "WorkOrder" or "Payment" or "Lease")
            .Should().OnlyContain(b => b.UnitId > 0,
                "unit-tied dashboard action items should deep-link into the unit Command Center");

        _executedSql.Should().Contain(command =>
            command.Contains("UNION", StringComparison.OrdinalIgnoreCase)
            && command.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && command.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Payments", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Leases", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Appointments", StringComparison.OrdinalIgnoreCase)
            && command.Contains("Inspections", StringComparison.OrdinalIgnoreCase),
            "daily briefing candidate ranking and cap must happen in one DB-side query before bullet formatting");
    }

    [Fact]
    public async Task ComposeAsync_ExcludesEndedFixedTermLeasePaymentsFromActiveAttention()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var endedLease = SeedLease(
            today,
            leaseNumber: "L-OLD",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-15),
            endDate: today.AddMonths(-3));
        var currentLease = SeedLease(
            today,
            leaseNumber: "L-CURRENT",
            propertyName: "Eastland 8-Plex",
            unitNumber: "4B",
            tenantFirstName: "Kevin",
            tenantLastName: "Brown",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6));

        _db.Payments.AddRange(
            new Payment
            {
                PortfolioId = PortfolioId,
                Lease = endedLease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 925m,
                DueDate = today.AddMonths(-10),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = PortfolioId,
                Lease = currentLease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 975m,
                DueDate = today.AddDays(-7),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _db.SaveChangesAsync();

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        briefing.Bullets.Should().ContainSingle(b =>
            b.Category == "RentLate" &&
            b.EntityId == _db.Payments.Single(p => p.LeaseId == currentLease.Id).Id);
        briefing.Bullets.Should().NotContain(b =>
            b.Category == "RentLate" &&
            b.Title.Contains("L-OLD", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComposeAsync_RentAttentionUsesTenantUnitPropertyLabelInsteadOfLeaseNumber()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var lease = SeedLease(
            today,
            leaseNumber: "L2024-003",
            propertyName: "Westview Four-Plex",
            unitNumber: "101",
            tenantFirstName: "Jordan",
            tenantLastName: "Smith",
            startDate: today.AddMonths(-6),
            endDate: today.AddMonths(6));

        _db.Payments.Add(new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Late,
            Amount = 925m,
            DueDate = today.AddDays(-7),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();

        var briefing = await _sut.ComposeAsync(PortfolioId, CancellationToken.None);

        var rentBullet = briefing.Bullets.Should().ContainSingle(b => b.Category == "RentLate").Subject;
        rentBullet.Title.Should().Contain("Jordan Smith");
        rentBullet.Title.Should().Contain("Westview Four-Plex");
        rentBullet.Title.Should().Contain("Unit 101");
        rentBullet.Title.Should().NotContain("L2024-003");
    }

    private void SeedBriefingData()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;

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
            StartDate = today.AddMonths(-1),
            EndDate = today.AddDays(30),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            RentDueDay = today.Day,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.AddRange(
            property,
            unit,
            tenant,
            lease,
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = property,
                Unit = unit,
                Title = "Water leak",
                Description = "Water is entering the kitchen ceiling.",
                Priority = WorkOrderPriority.Emergency,
                Status = WorkOrderStatus.New,
                RequestedAt = now,
                UpdatedAt = now,
            },
            new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Late,
                Amount = 1200m,
                DueDate = today.AddDays(-6),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Appointment
            {
                PortfolioId = PortfolioId,
                Property = property,
                Unit = unit,
                Title = "Showing",
                Type = AppointmentType.Showing,
                Status = AppointmentStatus.Scheduled,
                ScheduledStart = today.AddHours(14),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Inspection
            {
                PortfolioId = PortfolioId,
                Property = property,
                Unit = unit,
                Type = InspectionType.Routine,
                Status = InspectionStatus.Scheduled,
                ScheduledFor = today.AddDays(1).AddHours(9),
                CreatedAt = now,
                UpdatedAt = now,
            });
        _db.SaveChanges();
    }

    private Lease SeedLease(
        DateTime today,
        string leaseNumber,
        string propertyName,
        string unitNumber,
        string tenantFirstName,
        string tenantLastName,
        DateTime startDate,
        DateTime endDate)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
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
            UnitNumber = unitNumber,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = tenantFirstName,
            LastName = tenantLastName,
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
            StartDate = startDate,
            EndDate = endDate,
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            RentDueDay = today.Day,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

    private sealed class NoopLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) => Task.FromResult("");

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default)
            => Task.FromResult(new ExtractedFields());

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("end", "", [], 0, 0, "test"));
    }
}
