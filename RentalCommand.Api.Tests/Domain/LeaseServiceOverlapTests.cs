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
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task UpdateAsync_RejectsActiveLeaseDateEditThatOverlapsAnotherOccupyingLease()
    {
        var unit = SeedPropertyUnitTenant(out var tenantA, out var tenantB);
        var leaseA = SeedLease(unit, tenantA, "QA-2026-A", LeaseStatus.Active, Date(2026, 1, 1), Date(2026, 6, 1));
        SeedLease(unit, tenantB, "QA-2026-B", LeaseStatus.Active, Date(2026, 6, 1), Date(2026, 12, 31));

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
        var unit = SeedPropertyUnitTenant(out var tenantA, out var tenantB);
        var leaseA = SeedLease(unit, tenantA, "QA-2026-A", LeaseStatus.Active, Date(2026, 1, 1), Date(2026, 5, 1));
        SeedLease(unit, tenantB, "QA-2026-B", LeaseStatus.Active, Date(2026, 6, 1), Date(2026, 12, 31));

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            leaseA.Id,
            new UpdateLeaseRequest { EndDate = Date(2026, 6, 1) });

        updated.Should().NotBeNull();
        updated!.EndDate.Should().Be(Date(2026, 6, 1));
    }

    private Unit SeedPropertyUnitTenant(out Tenant tenantA, out Tenant tenantB)
    {
        var now = Date(2026, 1, 1);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Overlap Test Property",
            AddressLine1 = "1 Test Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            Status = UnitStatus.Occupied,
            CreatedAt = now,
            UpdatedAt = now,
        };
        tenantA = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Avery",
            LastName = "Leaseholder",
            CreatedAt = now,
            UpdatedAt = now,
        };
        tenantB = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Blair",
            LastName = "Leaseholder",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.AddRange(unit, tenantA, tenantB);
        _ctx.Db.SaveChanges();
        return unit;
    }

    private Lease SeedLease(Unit unit, Tenant tenant, string leaseNumber, LeaseStatus status, DateTime start, DateTime end)
    {
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = leaseNumber,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
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
