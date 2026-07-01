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

public sealed class OwnerEntityServiceDeleteTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly OwnerEntityService _sut;

    public OwnerEntityServiceDeleteTests()
    {
        _sut = new OwnerEntityService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task DeleteAsync_RejectsOwnerAssignedToProperties()
    {
        var owner = SeedOwner();
        SeedProperty(owner);

        var act = async () => await _sut.DeleteAsync(PortfolioId, owner.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("property").And.Contain("reassign");
        (await _sut.GetAsync(PortfolioId, owner.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_ClearsPropertyAssignmentsWhenExplicitlyRequested()
    {
        var owner = SeedOwner();
        var propertyId = SeedProperty(owner);

        var deleted = await _sut.DeleteAsync(
            PortfolioId,
            owner.Id,
            new DeleteOwnerEntityOptions { ClearPropertyAssignments = true });

        deleted.Should().BeTrue();
        _ctx.Db.ChangeTracker.Clear();
        (await _sut.GetAsync(PortfolioId, owner.Id)).Should().BeNull();
        _ctx.Db.Properties.Single(p => p.Id == propertyId).OwnerEntityId.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesUnreferencedOwner()
    {
        var owner = SeedOwner();

        var deleted = await _sut.DeleteAsync(PortfolioId, owner.Id);

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, owner.Id)).Should().BeNull();
    }

    private OwnerEntity SeedOwner()
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Owner To Delete",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private int SeedProperty(OwnerEntity owner)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = owner.Id,
            Name = "Owner Referenced Property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property.Id;
    }
}
