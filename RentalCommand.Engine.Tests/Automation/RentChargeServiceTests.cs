using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

public class RentChargeServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx;
    private readonly Mock<IMessagePublisher> _publisher;

    public RentChargeServiceTests()
    {
        _ctx       = new SqliteTestContext();
        _publisher = new Mock<IMessagePublisher>();
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task Disabled_CreatesNothing()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedActiveLease(property.Id, unit.Id, tenant.Id, monthlyRent: 1000m);

        var sut = BuildService(enable: false);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatesOneRentPaymentPerMonth_AndIsIdempotent()
    {
        var today = DateTime.UtcNow.Date;
        var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedActiveLease(
            property.Id,
            unit.Id,
            tenant.Id,
            monthlyRent: 1000m,
            rentDueDay: today.Day,
            startDate: start);

        var sut = BuildService(enable: true, leadDays: 5);

        // First call should create exactly one payment.
        var firstResult = await sut.GenerateAsync();
        firstResult.Should().Be(1);

        // Second call — idempotency — should create nothing.
        var secondResult = await sut.GenerateAsync();
        secondResult.Should().Be(0);

        // Verify exactly one Rent/Scheduled payment with the right attributes.
        var payments = _ctx.Db.Payments.ToList();
        payments.Should().HaveCount(1);

        var p = payments[0];
        p.PaymentType.Should().Be(PaymentType.Rent);
        p.Status.Should().Be(PaymentStatus.Scheduled);
        p.Amount.Should().Be(1000m);
        p.PeriodKey.Should().Be(today.ToString("yyyy-MM"));
    }

    [Fact]
    public async Task PastStartLease_CatchesUpMissingRentPeriodsThroughToday()
    {
        var today = DateTime.UtcNow.Date;
        var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedActiveLease(
            property.Id,
            unit.Id,
            tenant.Id,
            monthlyRent: 1000m,
            rentDueDay: 1,
            startDate: start);

        var sut = BuildService(enable: true, leadDays: 5);

        var result = await sut.GenerateAsync();

        result.Should().Be(3);
        _ctx.Db.Payments
            .OrderBy(p => p.PeriodKey)
            .Select(p => p.PeriodKey)
            .Should().Equal(
                start.ToString("yyyy-MM"),
                start.AddMonths(1).ToString("yyyy-MM"),
                today.ToString("yyyy-MM"));
    }

    [Fact]
    public async Task PastStartLease_WithRentTrackingStartDate_CatchesUpOnlyFromTrackingDate()
    {
        var today = DateTime.UtcNow.Date;
        var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-3);
        var rentTrackingStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedActiveLease(
            property.Id,
            unit.Id,
            tenant.Id,
            monthlyRent: 1000m,
            rentDueDay: 1,
            startDate: start,
            rentTrackingStartDate: rentTrackingStart);

        var sut = BuildService(enable: true, leadDays: 5);

        var result = await sut.GenerateAsync();

        result.Should().Be(2);
        _ctx.Db.Payments
            .OrderBy(p => p.PeriodKey)
            .Select(p => p.PeriodKey)
            .Should().Equal(
                rentTrackingStart.ToString("yyyy-MM"),
                today.ToString("yyyy-MM"));
    }

    [Fact]
    public async Task FutureStartLease_CreatesNothingEvenWhenCurrentPeriodIsInLeadWindow()
    {
        var today = DateTime.UtcNow.Date;
        var futureStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedActiveLease(
            property.Id,
            unit.Id,
            tenant.Id,
            monthlyRent: 1000m,
            rentDueDay: today.Day,
            startDate: futureStart);

        var sut = BuildService(enable: true, leadDays: 5);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyTenants_CreatesStaffAndTenantInAppNotifications_AndTargetsPush()
    {
        var today = DateTime.UtcNow.Date;
        var start = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        tenant.Email = "jane@example.test";
        tenant.Phone = "6145550101";
        SeedNotificationUsers(tenant.Id);
        SeedActiveLease(
            property.Id,
            unit.Id,
            tenant.Id,
            monthlyRent: 1000m,
            rentDueDay: today.Day,
            startDate: start);
        var publisher = new CapturingPublisher();
        var cfg = new NotificationsConfig
        {
            EnableRentCharges = true,
            RentChargeLeadDays = 5,
            NotifyTenants = true,
            ChannelPreferences =
            {
                [NotificationType.RentCharge] = new NotificationChannelPreference
                {
                    EnableInApp = true,
                    EnableEmail = false,
                    EnableSms = false,
                    EnablePush = true,
                },
            },
        };
        var sut = BuildService(cfg, publisher);

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        _ctx.Db.Notifications
            .Where(n => n.Type == "RentCharge")
            .Select(n => n.UserId)
            .Should().BeEquivalentTo(new int?[] { 10, 20 });
        _ctx.Db.Notifications.Single(n => n.UserId == 20).ActionUrl.Should().Be("/portal/payments");
        publisher.TargetedPushUserIds().Should().ContainEquivalentOf(new[] { 10 });
        publisher.TargetedPushUserIds().Should().ContainEquivalentOf(new[] { 20 });
    }

    // -----------------------------------------------------------------------
    // Helpers

    private RentChargeService BuildService(bool enable, int leadDays = 5)
    {
        var cfg = new NotificationsConfig
        {
            EnableRentCharges    = enable,
            RentChargeLeadDays   = leadDays,
            NotifyTenants        = false,
        };

        return BuildService(cfg, _publisher.Object);
    }

    private RentChargeService BuildService(NotificationsConfig cfg, IMessagePublisher publisher)
    {
        return new RentChargeService(
            _ctx.Db,
            publisher,
            new FakeNotificationSettingsService(cfg),
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System,
            new AppTimeZoneProvider(new ConfigurationBuilder().Build()),
            NullLogger<RentChargeService>.Instance);
    }

    private (Property property, Unit unit, Tenant tenant) SeedPropertyUnitTenant()
    {
        var now = DateTime.UtcNow;

        var property = new Property
        {
            PortfolioId  = 1,
            Name         = "Test Property",
            AddressLine1 = "123 Main St",
            City         = "Springfield",
            State        = "IL",
            PostalCode   = "62701",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        _ctx.Db.Properties.Add(property);

        var unit = new Unit
        {
            Property     = property,
            UnitNumber   = "1A",
            MarketRent   = 1000m,
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        _ctx.Db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName   = "Jane",
            LastName    = "Doe",
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        _ctx.Db.Tenants.Add(tenant);

        _ctx.Db.SaveChanges();
        return (property, unit, tenant);
    }

    private void SeedNotificationUsers(int tenantId)
    {
        _ctx.Db.Roles.AddRange(
            new IdentityRole<int>(nameof(UserRole.Admin))
            {
                Id = 1,
                NormalizedName = nameof(UserRole.Admin).ToUpperInvariant(),
            },
            new IdentityRole<int>(nameof(UserRole.Tenant))
            {
                Id = 2,
                NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
            });

        _ctx.Db.Users.AddRange(
            new ApplicationUser
            {
                Id = 10,
                PortfolioId = 1,
                UserName = "admin@example.test",
                NormalizedUserName = "ADMIN@EXAMPLE.TEST",
                Email = "admin@example.test",
                NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            },
            new ApplicationUser
            {
                Id = 20,
                PortfolioId = 1,
                TenantId = tenantId,
                UserName = "jane@example.test",
                NormalizedUserName = "JANE@EXAMPLE.TEST",
                Email = "jane@example.test",
                NormalizedEmail = "JANE@EXAMPLE.TEST",
            });

        _ctx.Db.UserRoles.AddRange(
            new IdentityUserRole<int> { UserId = 10, RoleId = 1 },
            new IdentityUserRole<int> { UserId = 20, RoleId = 2 });
        _ctx.Db.SaveChanges();
    }

    private Lease SeedActiveLease(
        int propertyId, int unitId, int tenantId,
        decimal monthlyRent, int rentDueDay = 1, DateTime? startDate = null, DateTime? rentTrackingStartDate = null)
    {
        var today = DateTime.UtcNow.Date;
        var lease = new Lease
        {
            PortfolioId     = 1,
            PropertyId      = propertyId,
            UnitId          = unitId,
            TenantId        = tenantId,
            LeaseNumber     = "L-001",
            Status          = LeaseStatus.Active,
            StartDate       = startDate ?? today.AddMonths(-6),
            EndDate         = today.AddMonths(6),
            MonthlyRent     = monthlyRent,
            SecurityDeposit = 0m,
            LateFeeAmount   = 0m,
            RentDueDay      = rentDueDay,
            RentTrackingStartDate = rentTrackingStartDate,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private sealed class CapturingPublisher : IMessagePublisher
    {
        private readonly List<string> _pushPayloads = [];

        public Task PublishAsync<TPayload>(
            int portfolioId,
            string messageType,
            TPayload payload,
            CancellationToken ct = default)
        {
            if (messageType == "push")
            {
                _pushPayloads.Add(JsonSerializer.Serialize(payload));
            }

            return Task.CompletedTask;
        }

        public IReadOnlyList<int[]> TargetedPushUserIds() =>
            _pushPayloads
                .Select(payload =>
                {
                    using var doc = JsonDocument.Parse(payload);
                    return doc.RootElement.GetProperty("userIds")
                        .EnumerateArray()
                        .Select(e => e.GetInt32())
                        .ToArray();
                })
                .ToList();
    }
}
