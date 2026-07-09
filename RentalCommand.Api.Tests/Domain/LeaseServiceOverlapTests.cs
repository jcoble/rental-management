using FluentAssertions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseServiceOverlapTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly LeaseService _sut;

    public LeaseServiceOverlapTests()
    {
        _sut = new LeaseService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IFileStorage>(),
            Mock.Of<ILeaseAgreementPdfGenerator>(),
            Mock.Of<IAuditTrailService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance,
            TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Theory]
    [InlineData(LeaseStatus.Active)]
    [InlineData(LeaseStatus.NoticeGiven)]
    public async Task CreateAsync_ActiveLeaseOverlappingOccupyingLease_ThrowsConflict(LeaseStatus existingStatus)
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", existingStatus, Date(2030, 1, 1), Date(2030, 12, 31));
        var newTenant = SeedTenant("Taylor", "Jones", "taylor.jones@example.local");

        var act = async () => await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-OVERLAP",
            Status = LeaseStatus.Active,
            StartDate = Date(2030, 6, 1),
            EndDate = Date(2031, 1, 1),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("L-EXISTING");
        _ctx.Db.Leases.Should().ContainSingle(l => l.LeaseNumber == "L-EXISTING");
        _ctx.Db.Leases.Should().NotContain(l => l.LeaseNumber == "L-OVERLAP");
    }

    [Fact]
    public async Task CreateAsync_DraftLeaseOverlappingOccupyingLease_AllowsLeaseWithoutBookingUnit()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active, Date(2030, 1, 1), Date(2030, 12, 31));
        var newTenant = SeedTenant("Jordan", "Parker", "jordan.parker@example.local");

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-DRAFT",
            Status = LeaseStatus.Draft,
            StartDate = Date(2030, 6, 1),
            EndDate = Date(2031, 1, 1),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        result!.Status.Should().Be(LeaseStatus.Draft);
    }

    [Fact]
    public async Task CreateAsync_ActiveLeaseStartingWhenExistingLeaseEnds_AllowsAdjacentTerms()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active, Date(2030, 1, 1), Date(2030, 6, 1));
        var newTenant = SeedTenant("Morgan", "Lee", "morgan.lee@example.local");

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-ADJACENT",
            Status = LeaseStatus.Active,
            StartDate = Date(2030, 6, 1),
            EndDate = Date(2031, 1, 1),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        result!.LeaseNumber.Should().Be("L-ADJACENT");
    }

    [Fact]
    public async Task CreateAsync_ActiveLeaseForNonVacantUnit_ThrowsConflict()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        unit.Status = UnitStatus.Occupied;
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-OCCUPIED-UNIT",
            Status = LeaseStatus.Active,
            StartDate = Date(2030, 6, 1),
            EndDate = Date(2031, 1, 1),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("vacant unit");
        _ctx.Db.Leases.Should().NotContain(l => l.LeaseNumber == "L-OCCUPIED-UNIT");
    }

    [Fact]
    public async Task CreateAsync_ActiveLeaseWithTenantAlreadyOnOccupyingLease_ThrowsConflict()
    {
        var (property, existingUnit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, existingUnit, tenant, "L-TENANT-ACTIVE", LeaseStatus.Active, Date(2030, 1, 1), Date(2030, 12, 31));
        var newUnit = new Unit
        {
            Property = property,
            UnitNumber = "2",
            MarketRent = 1275m,
            Status = UnitStatus.Vacant,
            CreatedAt = Date(2026, 1, 1),
            UpdatedAt = Date(2026, 1, 1),
        };
        _ctx.Db.Units.Add(newUnit);
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = newUnit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-TENANT-CONFLICT",
            Status = LeaseStatus.Active,
            StartDate = Date(2030, 6, 1),
            EndDate = Date(2031, 1, 1),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("L-TENANT-ACTIVE");
        _ctx.Db.Leases.Should().NotContain(l => l.LeaseNumber == "L-TENANT-CONFLICT");
    }

    [Fact]
    public async Task UpdateAsync_DraftToActiveWhenDatesOverlapOccupyingLease_ThrowsConflict()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active, Date(2030, 1, 1), Date(2030, 12, 31));
        var newTenant = SeedTenant("Avery", "Stone", "avery.stone@example.local");
        var draft = SeedLease(property, unit, newTenant, "L-DRAFT", LeaseStatus.Draft, Date(2030, 6, 1), Date(2031, 1, 1));

        var act = async () => await _sut.UpdateAsync(PortfolioId, draft.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("L-EXISTING");
        _ctx.Db.Leases.Single(l => l.Id == draft.Id).Status.Should().Be(LeaseStatus.Draft);
    }

    [Fact]
    public async Task UpdateAsync_RejectsActiveLeaseDateEditThatOverlapsAnotherOccupyingLease()
    {
        var (property, unit, tenantA) = SeedPropertyUnitTenant();
        var tenantB = SeedTenant("Blair", "Leaseholder", "blair.leaseholder@example.local");
        var leaseA = SeedLease(property, unit, tenantA, "QA-2026-A", LeaseStatus.Active, Date(2026, 1, 1), Date(2026, 6, 1));
        SeedLease(property, unit, tenantB, "QA-2026-B", LeaseStatus.Active, Date(2026, 6, 1), Date(2026, 12, 31));

        var act = async () => await _sut.UpdateAsync(
            PortfolioId,
            leaseA.Id,
            new UpdateLeaseRequest { EndDate = Date(2026, 7, 1) });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("QA-2026-B").And.Contain("overlapping");
    }

    [Fact]
    public async Task UpdateAsync_AllowsAdjacentActiveLeaseDateEdit()
    {
        var (property, unit, tenantA) = SeedPropertyUnitTenant();
        var tenantB = SeedTenant("Blair", "Leaseholder", "blair.leaseholder@example.local");
        var leaseA = SeedLease(property, unit, tenantA, "QA-2026-A", LeaseStatus.Active, Date(2026, 1, 1), Date(2026, 5, 1));
        SeedLease(property, unit, tenantB, "QA-2026-B", LeaseStatus.Active, Date(2026, 6, 1), Date(2026, 12, 31));

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            leaseA.Id,
            new UpdateLeaseRequest { EndDate = Date(2026, 6, 1) });

        updated.Should().NotBeNull();
        updated!.EndDate.Should().Be(Date(2026, 6, 1));
    }

    private (Property property, Unit unit, Tenant tenant) SeedPropertyUnitTenant()
    {
        var now = Date(2026, 1, 1);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Overlap Test Property",
            AddressLine1 = "100 Lease Ave",
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
        var now = Date(2026, 1, 1);
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

    private Lease SeedLease(
        Property property,
        Unit unit,
        Tenant tenant,
        string leaseNumber,
        LeaseStatus status,
        DateTime start,
        DateTime end)
    {
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = leaseNumber,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = start,
            UpdatedAt = start,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private static DateTime Date(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);
}
