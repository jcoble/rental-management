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
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>());
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

    private static CreateUnitRequest NewUnit(int propertyId, string unitNumber) => new()
    {
        PropertyId = propertyId,
        UnitNumber = unitNumber,
        Status = UnitStatus.Vacant,
    };
}
