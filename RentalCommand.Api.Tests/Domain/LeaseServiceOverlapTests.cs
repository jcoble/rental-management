using FluentAssertions;
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

public sealed class LeaseServiceOverlapTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly LeaseService _sut;

    public LeaseServiceOverlapTests()
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

    [Theory]
    [InlineData(LeaseStatus.Active)]
    [InlineData(LeaseStatus.NoticeGiven)]
    public async Task CreateAsync_ActiveLeaseOverlappingOccupyingLease_ThrowsConflict(LeaseStatus existingStatus)
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", existingStatus,
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var newTenant = SeedTenant("Taylor", "Jones", "taylor.jones@example.local");

        var act = async () => await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-OVERLAP",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
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
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active,
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var newTenant = SeedTenant("Jordan", "Parker", "jordan.parker@example.local");

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-DRAFT",
            Status = LeaseStatus.Draft,
            StartDate = new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
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
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active,
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var newTenant = SeedTenant("Morgan", "Lee", "morgan.lee@example.local");

        var result = await _sut.CreateAsync(PortfolioId, new CreateLeaseRequest
        {
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = newTenant.Id,
            LeaseNumber = "L-ADJACENT",
            Status = LeaseStatus.Active,
            StartDate = new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1275m,
            SecurityDeposit = 1275m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
        });

        result.Should().NotBeNull();
        result!.LeaseNumber.Should().Be("L-ADJACENT");
    }

    [Fact]
    public async Task UpdateAsync_DraftToActiveWhenDatesOverlapOccupyingLease_ThrowsConflict()
    {
        var (property, unit, tenant) = SeedPropertyUnitTenant();
        SeedLease(property, unit, tenant, "L-EXISTING", LeaseStatus.Active,
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        var newTenant = SeedTenant("Avery", "Stone", "avery.stone@example.local");
        var draft = SeedLease(property, unit, newTenant, "L-DRAFT", LeaseStatus.Draft,
            new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var act = async () => await _sut.UpdateAsync(PortfolioId, draft.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("L-EXISTING");
        _ctx.Db.Leases.Single(l => l.Id == draft.Id).Status.Should().Be(LeaseStatus.Draft);
    }

    private (Property property, Unit unit, Tenant tenant) SeedPropertyUnitTenant()
    {
        var now = DateTime.UtcNow;
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

    private Lease SeedLease(
        Property property,
        Unit unit,
        Tenant tenant,
        string leaseNumber,
        LeaseStatus status,
        DateTime start,
        DateTime end)
    {
        var now = DateTime.UtcNow;
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
