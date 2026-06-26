using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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

        return new RentChargeService(
            _ctx.Db,
            _publisher.Object,
            new FakeNotificationSettingsService(cfg),
            Mock.Of<IDataUpdateService>(),
            new ConfigurationBuilder().Build(),
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

    private Lease SeedActiveLease(
        int propertyId, int unitId, int tenantId,
        decimal monthlyRent, int rentDueDay = 1, DateTime? startDate = null)
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
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }
}
