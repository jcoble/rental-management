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
/// Pins the single occupancy definition: the dashboard must count a unit as "occupied" when
/// <c>Unit.Status == Occupied</c> — the same definition used by Analytics, the Occupancy report, and the
/// Properties list — NOT by whether the unit has an active lease. Regression guard for the divergence where
/// the dashboard showed 16/20 while every other surface showed 17/20.
/// </summary>
public class DashboardOccupancyDefinitionTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly DashboardService _sut;

    public DashboardOccupancyDefinitionTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
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

        _sut = new DashboardService(_db, new AuditDescriber(), TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Occupancy_CountsUnitStatusOccupied_NotActiveLeasePresence()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "P1",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        // Occupied-status unit WITHOUT any active lease — the exact case that diverged. Under the unified
        // definition this still counts as occupied.
        AddUnit(property.Id, UnitStatus.Occupied);
        // Occupied-status unit WITH an active lease — counts as occupied either way.
        var leasedUnit = AddUnit(property.Id, UnitStatus.Occupied);
        AddActiveLease(property.Id, leasedUnit.Id);
        // Reserved and Vacant units — neither is occupied.
        AddUnit(property.Id, UnitStatus.Reserved);
        AddUnit(property.Id, UnitStatus.Vacant);

        var dashboard = await _sut.GetDashboardAsync(PortfolioId);

        dashboard.Should().NotBeNull();
        var occ = dashboard!.Occupancy;
        occ.TotalUnits.Should().Be(4);
        occ.OccupiedUnits.Should().Be(2);   // both Occupied-status units, lease or not
        occ.ReservedUnits.Should().Be(1);
        occ.VacantUnits.Should().Be(1);
        occ.OccupancyRate.Should().Be(50.0);
    }

    private Unit AddUnit(int propertyId, UnitStatus status)
    {
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            PropertyId = propertyId,
            UnitNumber = Guid.NewGuid().ToString("N")[..6],
            MarketRent = 1000m,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Units.Add(unit);
        _db.SaveChanges();
        return unit;
    }

    private void AddActiveLease(int propertyId, int unitId)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "T",
            LastName = "Enant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        _db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            TenantId = tenant.Id,
            LeaseNumber = $"L-{Guid.NewGuid():N}"[..8],
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
    }
}
