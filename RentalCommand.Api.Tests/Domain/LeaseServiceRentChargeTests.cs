using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
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
            new AuditTrailService(_ctx.Db, new AuditScope(), TimeProvider.System),
            NullLogger<LeaseService>.Instance,
            TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateAsync_ActivePastStartLease_BackfillCreatesRentChargesThroughToday()
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
            RentTrackingStartMode = RentTrackingStartMode.BackfillFromLeaseStart,
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
    public async Task CreateAsync_ActivePastStartLease_DefaultCreatesCurrentDueChargeWithoutHistory()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-DEFAULT-FWD-001",
            Status = LeaseStatus.Active,
            StartDate = start,
            EndDate = FirstOfMonth(today).AddMonths(10),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = today.Day,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().ContainSingle();
        payments[0].PeriodKey.Should().Be(today.ToString("yyyy-MM"));
        payments[0].DueDate.Should().Be(today);
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
    public async Task CreateAsync_BlankLeaseNumber_GeneratesNextShortNumberForStartYear()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-2026-001",
            Status = LeaseStatus.Draft,
            StartDate = start.AddMonths(-12),
            EndDate = start.AddMonths(-1),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "   ",
            Status = LeaseStatus.Draft,
            StartDate = start,
            EndDate = start.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        result!.LeaseNumber.Should().Be("L-2026-002");

        var persisted = await _ctx.Db.Leases.AsNoTracking().SingleAsync(l => l.Id == result.Id);
        persisted.LeaseNumber.Should().Be("L-2026-002");
    }

    [Fact]
    public async Task CreateAsync_WithMultipleTenants_PersistsLeaseTenantMembershipsAndKeepsPrimaryTenant()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, primaryTenant) = SeedPropertyUnitTenant();
        var coTenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Jordan",
            LastName = "Smith",
            Email = "jordan@example.local",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Tenants.Add(coTenant);
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantIds = [primaryTenant.Id, coTenant.Id],
            LeaseNumber = "L-MULTI-001",
            Status = LeaseStatus.Draft,
            StartDate = today,
            EndDate = today.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 950m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(primaryTenant.Id);
        result.TenantIds.Should().Equal(primaryTenant.Id, coTenant.Id);
        var primaryName = $"{primaryTenant.FirstName} {primaryTenant.LastName}";
        result.TenantName.Should().Be($"{primaryName}, Jordan Smith");
        result.Tenants.Select(t => t.Name).Should().Equal(primaryName, "Jordan Smith");

        var memberships = await _ctx.Db.LeaseTenants
            .AsNoTracking()
            .Where(lt => lt.LeaseId == result.Id)
            .OrderByDescending(lt => lt.IsPrimary)
            .ThenBy(lt => lt.Id)
            .ToListAsync();

        memberships.Should().HaveCount(2);
        memberships.Select(m => m.TenantId).Should().Equal(primaryTenant.Id, coTenant.Id);
        memberships.Single(m => m.TenantId == primaryTenant.Id).IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_ActiveLeaseWithSecurityDeposit_CreatesDepositHolding()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-DEPOSIT-001",
            Status = LeaseStatus.Active,
            StartDate = today,
            EndDate = today.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 950m,
            LateFeeAmount = 75m,
            RentDueDay = today.Day,
        });

        result.Should().NotBeNull();
        var holding = await _ctx.Db.SecurityDepositHoldings
            .AsNoTracking()
            .SingleAsync(h => h.LeaseId == result!.Id);

        holding.PortfolioId.Should().Be(PortfolioId);
        holding.Amount.Should().Be(950m);
        holding.Status.Should().Be(SecurityDepositStatus.Held);
        holding.DeductionsJson.Should().Be("[]");
        holding.Notes.Should().Be("Created from lease security deposit.");
    }

    [Fact]
    public async Task CreateAsync_ActivePastStartLease_ForwardOnlyCreatesCurrentDueChargeWithoutHistory()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-FORWARD-001",
            Status = LeaseStatus.Active,
            StartDate = start,
            EndDate = FirstOfMonth(today).AddMonths(10),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = today.Day,
            RentTrackingStartMode = RentTrackingStartMode.ForwardOnly,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().ContainSingle();
        payments[0].PeriodKey.Should().Be(today.ToString("yyyy-MM"));
        payments[0].DueDate.Should().Be(today);
    }

    [Fact]
    public async Task CreateAsync_ActivePastStartLease_CustomCutoffCreatesOnlyCutoffAndLaterCharges()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-3);
        var cutoff = FirstOfMonth(today).AddMonths(-1);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-CUTOFF-001",
            Status = LeaseStatus.Active,
            StartDate = start,
            EndDate = FirstOfMonth(today).AddMonths(10),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            RentTrackingStartMode = RentTrackingStartMode.CustomCutoffDate,
            RentTrackingStartDate = cutoff,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().HaveCount(2);
        payments.Select(p => p.PeriodKey).Should().Equal(
            cutoff.ToString("yyyy-MM"),
            today.ToString("yyyy-MM"));
    }

    [Fact]
    public async Task CreateAsync_OpeningBalanceMode_CreatesOpeningBalanceAndCurrentChargeOnly()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-4);
        var asOfDate = today.AddDays(-1);
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-OPENING-001",
            Status = LeaseStatus.Active,
            StartDate = start,
            EndDate = FirstOfMonth(today).AddMonths(10),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = today.Day,
            RentTrackingStartMode = RentTrackingStartMode.OpeningBalanceOnly,
            OpeningBalanceAmount = 2400m,
            OpeningBalanceAsOfDate = asOfDate,
            OpeningBalanceNote = "Prior system balance",
        });

        result.Should().NotBeNull();

        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == result!.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();
        payments.Should().ContainSingle();
        payments[0].PeriodKey.Should().Be(today.ToString("yyyy-MM"));

        var opening = await _ctx.Db.OpeningBalances
            .AsNoTracking()
            .SingleAsync(o => o.LeaseId == result!.Id);
        opening.Amount.Should().Be(2400m);
        opening.AsOfDate.Should().Be(asOfDate);
        opening.Note.Should().Be("Prior system balance");

        var lease = await _ctx.Db.Leases.AsNoTracking().SingleAsync(l => l.Id == result!.Id);
        lease.RentTrackingStartDate.Should().Be(today);
    }

    [Fact]
    public async Task CreateAsync_OpeningBalanceFieldsWithoutOpeningMode_Throws()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, tenant) = SeedPropertyUnitTenant();

        var act = () => _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-OPENING-INVALID-001",
            Status = LeaseStatus.Active,
            StartDate = today,
            EndDate = today.AddMonths(12),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = today.Day,
            RentTrackingStartMode = RentTrackingStartMode.ForwardOnly,
            OpeningBalanceAmount = 2400m,
            OpeningBalanceAsOfDate = today,
        });

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("Opening balance fields can only be used with the opening balance rent tracking option.");
    }

    [Fact]
    public async Task UpdateAsync_ActivatingPastStartLease_BackfillCreatesRentChargesThroughToday()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, start);

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
            RentTrackingStartMode = RentTrackingStartMode.BackfillFromLeaseStart,
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

    [Fact]
    public async Task UpdateAsync_WithMultipleTenants_ReplacesMembershipsAndPrimaryTenant()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, originalTenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, originalTenant, LeaseStatus.Draft, today);
        var newPrimary = SeedTenant("Maria", "Lee", "maria@example.local");
        var coTenant = SeedTenant("Jordan", "Smith", "jordan@example.local");

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            TenantIds = [newPrimary.Id, coTenant.Id],
        });

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(newPrimary.Id);
        result.TenantIds.Should().Equal(newPrimary.Id, coTenant.Id);
        result.TenantName.Should().Be("Maria Lee, Jordan Smith");
        result.Tenants.Select(t => t.Name).Should().Equal("Maria Lee", "Jordan Smith");

        var memberships = await _ctx.Db.LeaseTenants
            .AsNoTracking()
            .Where(lt => lt.LeaseId == lease.Id)
            .OrderByDescending(lt => lt.IsPrimary)
            .ThenBy(lt => lt.Id)
            .ToListAsync();

        memberships.Should().HaveCount(2);
        memberships.Select(m => m.TenantId).Should().Equal(newPrimary.Id, coTenant.Id);
        memberships.Single(m => m.TenantId == newPrimary.Id).IsPrimary.Should().BeTrue();
        memberships.Should().NotContain(m => m.TenantId == originalTenant.Id);
    }

    [Fact]
    public async Task UpdateAsync_ActivatingLeaseWithSecurityDeposit_CreatesDepositHolding()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, today);

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        result.Should().NotBeNull();
        var holding = await _ctx.Db.SecurityDepositHoldings
            .AsNoTracking()
            .SingleAsync(h => h.LeaseId == lease.Id);

        holding.Amount.Should().Be(1275m);
        holding.Status.Should().Be(SecurityDepositStatus.Held);
    }

    [Fact]
    public async Task UpdateAsync_ActivatingLeaseWithExistingDepositHolding_DoesNotDuplicate()
    {
        var today = DateTime.UtcNow.Date;
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, today);
        _ctx.Db.SecurityDepositHoldings.Add(new SecurityDepositHolding
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            Amount = 800m,
            Status = SecurityDepositStatus.Held,
            HeldAt = today,
            DeductionsJson = "[]",
            Notes = "Entered manually.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        result.Should().NotBeNull();
        var holdings = await _ctx.Db.SecurityDepositHoldings
            .AsNoTracking()
            .Where(h => h.LeaseId == lease.Id)
            .ToListAsync();

        holdings.Should().ContainSingle();
        holdings[0].Amount.Should().Be(800m);
        holdings[0].Notes.Should().Be("Entered manually.");
    }

    [Fact]
    public async Task UpdateAsync_ActivatingPastStartLease_ForwardOnlyCreatesCurrentDueChargeWithoutHistory()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, start, today.Day);

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
            RentTrackingStartMode = RentTrackingStartMode.ForwardOnly,
        });

        result.Should().NotBeNull();
        var payments = await _ctx.Db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId == lease.Id)
            .OrderBy(p => p.PeriodKey)
            .ToListAsync();

        payments.Should().ContainSingle();
        payments[0].PeriodKey.Should().Be(today.ToString("yyyy-MM"));
        payments[0].DueDate.Should().Be(today);
    }

    [Fact]
    public async Task UpdateAsync_ActivatingPastStartLease_DefaultCreatesCurrentDueChargeWithoutHistory()
    {
        var today = DateTime.UtcNow.Date;
        var start = FirstOfMonth(today).AddMonths(-2);
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        var lease = SeedLease(property, unit, tenant, LeaseStatus.Draft, start, today.Day);

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

        payments.Should().ContainSingle();
        payments[0].PeriodKey.Should().Be(today.ToString("yyyy-MM"));
        payments[0].DueDate.Should().Be(today);
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

    private Tenant SeedTenant(string firstName, string lastName, string email)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Lease SeedLease(Property property, Unit unit, Tenant tenant, LeaseStatus status, DateTime start, int rentDueDay = 1)
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
            RentDueDay = rentDueDay,
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
