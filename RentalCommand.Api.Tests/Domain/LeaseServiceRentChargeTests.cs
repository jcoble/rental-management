using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseServiceRentChargeTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly LeaseService _sut;

    public LeaseServiceRentChargeTests()
    {
        _sut = new LeaseService(
            _ctx.Db,
            new NoopDataUpdateService(),
            Mock.Of<IFileStorage>(),
            Mock.Of<ILeaseAgreementPdfGenerator>(),
            new AuditTrailService(_ctx.Db, new AuditScope()),
            NullLogger<LeaseService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateAsync_ActivePastStartLease_CreatesRentChargesThroughToday()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-PAST-001",
            Status = LeaseStatus.Active,
            StartDate = start,
            EndDate = FirstOfMonth(today).AddMonths(10),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().HaveCount(3);
        payments.Select(p => p.PeriodKey).Should().Equal(
            start.ToString("yyyy-MM"),
            start.AddMonths(1).ToString("yyyy-MM"),
            today.ToString("yyyy-MM"));
        payments.Should().OnlyContain(p =>
            p.PaymentType == PaymentType.Rent
            && p.Status == PaymentStatus.Scheduled
            && p.Amount == 1275m);
    }

    [Fact]
    public async Task CreateAsync_ActiveFutureStartLease_CreatesNoRentCharges()
    {
        var today = DateTime.UtcNow.Date;
        var futureStart = FirstOfMonth(today).AddMonths(1);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-FUTURE-001",
            Status = LeaseStatus.Active,
            StartDate = futureStart,
            EndDate = futureStart.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .ToListAsync();
        payments.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ActivatingPastStartLease_CreatesRentChargesThroughToday()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, start);

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == lease.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().HaveCount(3);
        payments.Select(p => p.PeriodKey).Should().Equal(
            start.ToString("yyyy-MM"),
            start.AddMonths(1).ToString("yyyy-MM"),
            today.ToString("yyyy-MM"));
        payments.Should().OnlyContain(p =>
            p.PaymentType == PaymentType.Rent
            && p.Status == PaymentStatus.Scheduled
            && p.Amount == 1275m);
    }

    private static DateTime FirstOfMonth(DateTime value)
        => new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private (Property property, Unit unit, Tenant tenant) SeedPropertyUnitTenant()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Eastland 8-Plex",
            AddressLine1 = "100 East Ave",
            City = "Cincinnati",
            State = "OH",
            PostalCode = "45202",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            MarketRent = 1275m,
            Status = UnitStatus.Vacant,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Kevin",
            LastName = "Brown",
            Email = "kevin.brown@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Properties.Add(property);
        _ctx.Db.Units.Add(unit);
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        return (property, unit, tenant);
    }

    private Lease SeedLease(Property property, Unit unit, Tenant tenant, LeaseStatus status, DateTime start)
    {
        var now = DateTime.UtcNow;
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = $"L-{Guid.NewGuid():N}",
            Status = status,
            StartDate = start,
            EndDate = start.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
