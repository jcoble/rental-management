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

/// <summary>
/// Covers the duplicate unit-number guard on <see cref="UnitService.CreateAsync"/> /
/// <see cref="UnitService.UpdateAsync"/> — a clear, field-specific 409 instead of the opaque
/// unique-index conflict the DB would otherwise surface.
/// </summary>
public class UnitServiceCreateTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx;
    private readonly UnitService _sut;

    public UnitServiceCreateTests()
    {
        _ctx = new SqliteTestContext();
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateAsync_ThrowsClear409WhenUnitNumberDuplicatedOnProperty()
    {
        var property = SeedProperty();
        await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "101"));

        var act = async () => await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "101"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("\"101\"").And.Contain("already exists");
        _ctx.Db.Units.Count(u => u.PropertyId == property.Id).Should().Be(1, "the duplicate was not inserted");
    }

    [Fact]
    public async Task CreateAsync_AllowsSameUnitNumberOnDifferentProperty()
    {
        var propertyA = SeedProperty("Property A");
        var propertyB = SeedProperty("Property B");
        await _sut.CreateAsync(PortfolioId, NewUnit(propertyA.Id, "101"));

        var created = await _sut.CreateAsync(PortfolioId, NewUnit(propertyB.Id, "101"));

        created.Should().NotBeNull();
        created!.UnitNumber.Should().Be("101");
    }

    [Fact]
    public async Task UpdateAsync_ThrowsClear409WhenRenamingToAnExistingUnitNumber()
    {
        var property = SeedProperty();
        await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "101"));
        var second = await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "102"));

        var act = async () =>
            await _sut.UpdateAsync(PortfolioId, second!.Id, new UpdateUnitRequest { UnitNumber = "101" });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("\"101\"").And.Contain("already exists");
    }

    [Fact]
    public async Task UpdateAsync_AllowsKeepingTheSameUnitNumber()
    {
        var property = SeedProperty();
        var unit = await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "101"));

        var updated = await _sut.UpdateAsync(
            PortfolioId, unit!.Id, new UpdateUnitRequest { UnitNumber = "101", MarketRent = 1500m });

        updated.Should().NotBeNull();
        updated!.UnitNumber.Should().Be("101");
        updated.MarketRent.Should().Be(1500m);
    }

    [Fact]
    public async Task UpdateAsync_AllowsMarketRentButBlocksStatusChangeWhenUnitHasCurrentLease()
    {
        var property = SeedProperty();
        var unit = await _sut.CreateAsync(PortfolioId, NewUnit(property.Id, "101", UnitStatus.Occupied));
        SeedLease(unit!.Id, property.Id, LeaseStatus.Active);

        var rentUpdate = await _sut.UpdateAsync(
            PortfolioId, unit.Id, new UpdateUnitRequest { MarketRent = 1500m });

        rentUpdate.Should().NotBeNull();
        rentUpdate!.MarketRent.Should().Be(1500m);

        var act = async () => await _sut.UpdateAsync(
            PortfolioId, unit.Id, new UpdateUnitRequest { Status = UnitStatus.Vacant });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("current lease");
    }

    private Property SeedProperty(string name = "Test Property")
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = "1 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private void SeedLease(int unitId, int propertyId, LeaseStatus status)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Active",
            LastName = "Resident",
            Email = "active.resident@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            Tenant = tenant,
            LeaseNumber = "L-CURRENT",
            Status = status,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private static CreateUnitRequest NewUnit(
        int propertyId,
        string unitNumber,
        UnitStatus status = UnitStatus.Vacant) => new()
    {
        PropertyId = propertyId,
        UnitNumber = unitNumber,
        Status = status,
    };
}
