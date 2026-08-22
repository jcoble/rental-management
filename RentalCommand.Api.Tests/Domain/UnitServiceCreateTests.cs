using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
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
[Collection(MigratedPostgreSqlCollection.Name3)]
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
        _sut = _services.GetRequiredService<UnitService>();
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

    [Theory]
    [InlineData(PropertyType.SingleFamily, true)]
    [InlineData(PropertyType.MultiFamily, true)]
    [InlineData(PropertyType.Condo, true)]
    [InlineData(PropertyType.Townhome, true)]
    [InlineData(PropertyType.Storage, false)]
    [InlineData(PropertyType.Parking, false)]
    [InlineData(PropertyType.Commercial, false)]
    public async Task CreateAsync_applies_bed_bath_requirement_by_property_type(
        PropertyType propertyType, bool requiresResidentialDetails)
    {
        var property = SeedProperty($"{propertyType} property", propertyType);
        var request = NewUnit(property.Id, "101");
        request.Bedrooms = null;
        request.Bathrooms = null;

        if (requiresResidentialDetails)
        {
            var act = async () => await _sut.CreateAsync(
                _scope, request, Guid.NewGuid().ToString("N"));

            var ex = await act.Should().ThrowAsync<DomainValidationException>();
            ex.Which.Message.Should().Be("Bedrooms and bathrooms are required for residential dwellings.");
            (await _ctx.Db.Units.CountAsync(unit => unit.PropertyId == property.Id)).Should().Be(0);
        }
        else
        {
            var created = await _sut.CreateAsync(
                _scope, request, Guid.NewGuid().ToString("N"));

            created.Should().NotBeNull();
            created!.Bedrooms.Should().Be(0m);
            created.Bathrooms.Should().Be(0m);
        }
    }

    [Fact]
    public async Task UpdateAsync_rejects_explicitly_clearing_beds_or_baths_on_residential_property()
    {
        var property = SeedProperty("Residential property", PropertyType.SingleFamily);
        var unit = await _sut.CreateAsync(_scope, NewUnit(property.Id, "101"), Guid.NewGuid().ToString("N"));

        var act = async () => await _sut.UpdateAsync(
            _scope, unit!.Id, new UpdateUnitRequest { Bedrooms = null }, Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Be("Bedrooms and bathrooms are required for residential dwellings.");
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

    [Fact]
    public async Task UpdateAsync_UnchangedFieldsDoNotTouchUnitOrEmitAuditOrOutbox()
    {
        var property = SeedProperty();
        var create = NewUnit(property.Id, "101");
        create.FloorPlan = "Garden 1B";
        create.Bedrooms = 1m;
        create.Bathrooms = 1m;
        create.SquareFeet = 625;
        create.MarketRent = 1135m;
        create.Notes = "North garden entrance";
        var unit = await _sut.CreateAsync(_scope, create, Guid.NewGuid().ToString("N"));
        var updatedAt = unit!.UpdatedAt;
        var auditCount = _ctx.Db.AtomicAuditLogs.Count(row =>
            row.EntityType == nameof(Unit) && row.EntityId == unit.Id);
        var outboxCount = _ctx.Db.OutboxMessages.Count();
        var request = new UpdateUnitRequest
        {
            UnitNumber = unit.UnitNumber,
            FloorPlan = unit.FloorPlan,
            Bedrooms = unit.Bedrooms,
            Bathrooms = unit.Bathrooms,
            SquareFeet = unit.SquareFeet,
            MarketRent = unit.MarketRent,
            Notes = unit.Notes,
        };

        var unchanged = await _sut.UpdateAsync(
            _scope, unit.Id, request, Guid.NewGuid().ToString("N"));

        unchanged.Should().NotBeNull();
        unchanged!.UpdatedAt.Should().Be(updatedAt);
        _ctx.Db.AtomicAuditLogs.Count(row =>
            row.EntityType == nameof(Unit) && row.EntityId == unit.Id).Should().Be(auditCount);
        _ctx.Db.OutboxMessages.Count().Should().Be(outboxCount);
    }

    [Fact]
    public async Task UpdateAsync_ExplicitNullClearsOptionalTextAndExactReplayMutatesOnce()
    {
        var property = SeedProperty();
        var create = NewUnit(property.Id, "101");
        create.FloorPlan = "Garden";
        create.Notes = "Assigned parking";
        var unit = await _sut.CreateAsync(_scope, create, Guid.NewGuid().ToString("N"));
        var operationKey = Guid.NewGuid().ToString("N");
        var request = new UpdateUnitRequest { FloorPlan = null, Notes = null };
        var writeRequest = RentalCrudWriteSupport.UnitUpdateRequest(
            _scope, unit!.Id, operationKey, request);
        var identity = new AtomicCommandIdentity(
            "rental.unit.update", RentalCrudWriteSupport.IdempotencyKey(writeRequest));

        var updated = await _sut.UpdateAsync(_scope, unit.Id, request, operationKey);
        var replayed = await _sut.UpdateAsync(_scope, unit.Id, request, operationKey);

        updated.Should().NotBeNull();
        updated!.FloorPlan.Should().BeNull();
        updated.Notes.Should().BeNull();
        replayed.Should().BeEquivalentTo(updated);
        _ctx.Db.Units.Single(row => row.Id == unit.Id).Should().Match<Unit>(
            row => row.FloorPlan == null && row.Notes == null);
        _ctx.Db.AtomicCommandReceipts.Count(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey).Should().Be(1);
    }

    private Property SeedProperty(string name = "Test Property", PropertyType propertyType = PropertyType.MultiFamily)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            PropertyType = propertyType,
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
        Bedrooms = 1m,
        Bathrooms = 1m,
    };
}
