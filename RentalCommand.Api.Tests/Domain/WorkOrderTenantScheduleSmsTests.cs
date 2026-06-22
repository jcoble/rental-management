using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Pins the tenant arrival-window SMS to the landlord's LOCAL time (the offset the client sent) with no
/// "(UTC)" label. Regression for the coherence bug where a zoneless wall-clock was relabeled UTC and the
/// tenant was texted a wrong time stamped "(UTC)".
/// </summary>
public class WorkOrderTenantScheduleSmsTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly RecordingPublisher _publisher = new();
    private readonly WorkOrderService _workOrders;

    public WorkOrderTenantScheduleSmsTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new WorkOrderSmsTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "America/New_York",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _workOrders = new WorkOrderService(
            _db,
            new NoopDataUpdate(),
            _publisher,
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task CreateAsync_TextsTenant_InLandlordLocalTime_NoUtcLabel()
    {
        var property = SeedProperty();
        var tenant = SeedTenant("Maria", "Tenant", phone: "+16145551212");

        // Eastern landlord schedules a 2 PM – 4 PM window. Wire value carries the -04:00 (EDT) offset.
        var start = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.FromHours(-4));
        var end = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.FromHours(-4));

        var created = await _workOrders.CreateAsync(PortfolioId, new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            TenantId = tenant.Id,
            Title = "Dishwasher repair",
            Description = "Not draining",
            Status = WorkOrderStatus.New,
            ScheduledFor = start,
            ScheduledWindowEnd = end,
        });

        created.Should().NotBeNull();

        // Stored as the true UTC instant (2 PM EDT == 18:00Z).
        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created!.Id);
        entity.ScheduledFor.Should().Be(new DateTime(2026, 6, 20, 18, 0, 0, DateTimeKind.Utc));
        entity.ScheduledWindowEnd.Should().Be(new DateTime(2026, 6, 20, 20, 0, 0, DateTimeKind.Utc));

        // The tenant SMS renders the landlord's LOCAL window (2:00 PM – 4:00 PM), never UTC.
        var message = _publisher.LastSms.Should().NotBeNull().And.Subject as string;
        message.Should().Contain("2:00 PM");
        message.Should().Contain("4:00 PM");
        message.Should().NotContain("(UTC)");
        message.Should().NotContain("6:00 PM"); // the UTC-relabeled wrong time must not appear
        message.Should().NotContain("8:00 PM");
    }

    [Fact]
    public async Task CreateAsync_NoWindowEnd_DoesNotTextTenant()
    {
        var property = SeedProperty();
        var tenant = SeedTenant("Sam", "Renter", phone: "+16145559999");

        await _workOrders.CreateAsync(PortfolioId, new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            TenantId = tenant.Id,
            Title = "No window",
            Description = "Start only",
            Status = WorkOrderStatus.New,
            ScheduledFor = new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.FromHours(-4)),
        });

        _publisher.SmsCount.Should().Be(0);
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private Tenant SeedTenant(string first, string last, string phone)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = first,
            LastName = last,
            Phone = phone,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant;
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public int SmsCount { get; private set; }
        public string? LastSms { get; private set; }

        public Task PublishAsync<TPayload>(int portfolioId, string messageType, TPayload payload, CancellationToken ct = default)
        {
            if (messageType == "sms" && payload is not null)
            {
                SmsCount++;
                // Payload is an anonymous { to, message }; read message via reflection.
                LastSms = payload.GetType().GetProperty("message")?.GetValue(payload) as string;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopDataUpdate : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

internal sealed class WorkOrderSmsTestDbContext : RentalCommandDbContext
{
    public WorkOrderSmsTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<WorkOrder>().Property(e => e.ExtractedData).HasColumnType("TEXT");
    }
}
