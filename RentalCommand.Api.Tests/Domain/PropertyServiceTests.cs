using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class PropertyServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly PropertyService _sut;

    public PropertyServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new PropertyService(_ctx.Db, Mock.Of<IDataUpdateService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedProperties("Alpha", "Bravo", "Charlie", "Delta", "Echo");

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new PropertyListQuery
        {
            Sort = "name",
            Skip = 2,
            Take = 2,
        });

        result.TotalCount.Should().Be(5);
        result.Skip.Should().Be(2);
        result.Take.Should().Be(2);
        result.Items.Select(p => p.Name).Should().Equal("Charlie", "Delta");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Name\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenPropertyStillHasLiveUnits()
    {
        var property = SeedPropertyWithUnit(out _);

        var act = async () => await _sut.DeleteAsync(PortfolioId, property.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("Remove the unit");
        (await _sut.GetAsync(PortfolioId, property.Id))
            .Should().NotBeNull("a property with live units must not be deleted");
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenPropertyHasOccupyingLeaseButNoLiveUnit()
    {
        var property = SeedPropertyWithUnit(out var unit);
        SeedOccupyingLease(property, unit, LeaseStatus.NoticeGiven);
        // Soft-delete the unit so the unit guard passes and only the lease safety-net guard can fire —
        // the exact orphan scenario (occupying lease whose unit is already gone).
        unit.DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, property.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("active lease");
        (await _sut.GetAsync(PortfolioId, property.Id))
            .Should().NotBeNull("a property with an occupying lease must not be deleted");
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesWhenNoUnitsOrOccupyingLeases()
    {
        SeedProperties("Standalone");
        var property = _ctx.Db.Properties.Single(p => p.Name == "Standalone");

        var deleted = await _sut.DeleteAsync(PortfolioId, property.Id);

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, property.Id))
            .Should().BeNull("a property with no children is soft-deleted");
    }

    private Property SeedPropertyWithUnit(out Unit unit)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Occupied Property",
            AddressLine1 = "1 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        unit = new Unit
        {
            Property = property,
            UnitNumber = "101",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        return property;
    }

    private void SeedOccupyingLease(Property property, Unit unit, LeaseStatus status)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Occupant",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            TenantId = tenant.Id,
            LeaseNumber = "L-1",
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

    private void SeedProperties(params string[] names)
    {
        var now = DateTime.UtcNow;
        foreach (var name in names)
        {
            _ctx.Db.Properties.Add(new Property
            {
                PortfolioId = PortfolioId,
                Name = name,
                AddressLine1 = $"{name} Street",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.SaveChanges();
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
