using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the duplicate unit-number guard on <see cref="UnitService.CreateAsync"/> /
/// <see cref="UnitService.UpdateAsync"/> — a clear, field-specific 409 instead of the opaque
/// unique-index conflict the DB would otherwise surface.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name)]
public class UnitServiceCreateTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private UnitService _sut = null!;
    private WorkspaceReadScope _scope;

    public UnitServiceCreateTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _services = AtomicDomainTestKernel.CreateForRentalCrudPostgreSql(_ctx.ConnectionString);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(UnitServiceCreateTests));
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(),
            TimeProvider.System, _services.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_ThrowsClear409WhenUnitNumberDuplicatedOnProperty()
    {
        var property = SeedProperty();
        await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));

        var act = async () => await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));

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
        await _sut.CreateAsync(_scope, NewUnit(propertyA.Id, "101"), Guid.NewGuid().ToString("N"));

        var created = await _sut.CreateAsync(_scope, NewUnit(propertyB.Id, "101"), Guid.NewGuid().ToString("N"));

        created.Should().NotBeNull();
        created!.UnitNumber.Should().Be("101");
    }

    [Fact]
    public async Task UpdateAsync_ThrowsClear409WhenRenamingToAnExistingUnitNumber()
    {
        var property = SeedProperty();
        await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));
        var second = await _sut.CreateAsync(_scope, NewUnit(property.Id, "102"), Guid.NewGuid().ToString("N"));

        var act = async () =>
            await _sut.UpdateAsync(_scope, second!.Id, new UpdateUnitRequest { UnitNumber = "101" }, Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("\"101\"").And.Contain("already exists");
    }

    [Fact]
    public async Task UpdateAsync_AllowsKeepingTheSameUnitNumber()
    {
        var property = SeedProperty();
        var unit = await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));

        var updated = await _sut.UpdateAsync(
            _scope, unit!.Id, new UpdateUnitRequest { UnitNumber = "101", MarketRent = 1500m }, Guid.NewGuid().ToString("N"));

        updated.Should().NotBeNull();
        updated!.UnitNumber.Should().Be("101");
        updated.MarketRent.Should().Be(1500m);
    }

    [Fact]
    public async Task UpdateAsync_AllowsMarketRentWithoutAcceptingMutableOccupancyStatus()
    {
        var property = SeedProperty();
        var unit = await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));

        var rentUpdate = await _sut.UpdateAsync(
            _scope, unit!.Id, new UpdateUnitRequest { MarketRent = 1500m }, Guid.NewGuid().ToString("N"));

        rentUpdate.Should().NotBeNull();
        rentUpdate!.MarketRent.Should().Be(1500m);
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
    };
}
