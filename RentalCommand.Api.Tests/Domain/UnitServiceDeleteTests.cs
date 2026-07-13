using FluentAssertions;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class UnitServiceDeleteTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private UnitService _sut = null!;

    public UnitServiceDeleteTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>(), Mock.Of<IAuditTrailService>(), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task DeleteAsync_RejectsUnitWithLeaseHistory()
    {
        var unit = SeedUnit();
        SeedLease(unit);

        var act = async () => await _sut.DeleteAsync(PortfolioId, unit.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("lease").And.Contain("history");
        (await _sut.GetAsync(PortfolioId, unit.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RejectsUnitWithWorkOrderHistory()
    {
        var unit = SeedUnit();
        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Title = "Historical repair",
            Description = "Historical repair",
            RequestedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, unit.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("work order").And.Contain("history");
        (await _sut.GetAsync(PortfolioId, unit.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RejectsUnitWithApplicationHistory()
    {
        var unit = SeedUnit();
        _ctx.Db.RentalApplications.Add(new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            FirstName = "Applied",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, unit.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("application").And.Contain("history");
        (await _sut.GetAsync(PortfolioId, unit.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesEmptyUnit()
    {
        var unit = SeedUnit();

        var deleted = await _sut.DeleteAsync(PortfolioId, unit.Id);

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, unit.Id)).Should().BeNull();
    }

    private Unit SeedUnit()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Unit Delete Property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = Guid.NewGuid().ToString("N")[..8],
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        return unit;
    }

    private void SeedLease(Unit unit)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.LeaseManagements.Add(new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            RelationshipNumber = "HISTORY-RELATIONSHIP",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        });
        _ctx.Db.SaveChanges();
    }
}
