using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
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
    private readonly WorkspaceReadScope _scope;

    public PropertyServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _ctx.Db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();
        _scope = SeedAdministratorScope();
        _sut = new PropertyService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    private WorkspaceReadScope SeedAdministratorScope()
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "property-service@example.test",
            NormalizedUserName = "PROPERTY-SERVICE@EXAMPLE.TEST",
            Email = "property-service@example.test",
            NormalizedEmail = "PROPERTY-SERVICE@EXAMPLE.TEST",
            DisplayName = "Property Service Test Administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        _ctx.Db.AddRange(assignment, session);
        _ctx.Db.SaveChanges();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedProperties("Alpha", "Bravo", "Charlie", "Delta", "Echo");

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, new PropertyListQuery
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

    [Theory]
    [InlineData(PropertyType.SingleFamily)]
    [InlineData(PropertyType.Condo)]
    [InlineData(PropertyType.Townhome)]
    public async Task CreateAsync_CreatesCanonicalUnitForPropertyUnitTypes(PropertyType propertyType)
    {
        var created = await _sut.CreateAsync(PortfolioId, NewProperty("293 Mallard Point Dr", propertyType));

        created.Should().NotBeNull();
        created!.UnitCount.Should().Be(1);

        var unit = _ctx.Db.Units.Single(u => u.PropertyId == created.Id);
        unit.UnitNumber.Should().Be("293 Mallard Point Dr");
        unit.MarketRent.Should().Be(0m);
    }

    [Theory]
    [InlineData(PropertyType.MultiFamily)]
    [InlineData(PropertyType.MixedUse)]
    [InlineData(PropertyType.Commercial)]
    public async Task CreateAsync_DoesNotCreateCanonicalUnitForUnitizedPropertyTypes(PropertyType propertyType)
    {
        var created = await _sut.CreateAsync(PortfolioId, NewProperty("Westview Four-Plex", propertyType));

        created.Should().NotBeNull();
        created!.UnitCount.Should().Be(0);
        _ctx.Db.Units.Where(u => u.PropertyId == created.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_CreatesCanonicalUnitWhenPropertyUnitTypeHasNoUnits()
    {
        var property = SeedProperty("Standalone Home", PropertyType.MultiFamily);

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            property.Id,
            new UpdatePropertyRequest { PropertyType = PropertyType.SingleFamily });

        updated.Should().NotBeNull();
        updated!.UnitCount.Should().Be(1);
        _ctx.Db.Units.Single(u => u.PropertyId == property.Id).UnitNumber.Should().Be("Standalone Home");
    }

    [Fact]
    public async Task UpdateAsync_RenamesExistingCanonicalUnitWhenPropertyNameChanges()
    {
        var created = await _sut.CreateAsync(PortfolioId, NewProperty("Old Home Name", PropertyType.SingleFamily));

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            created!.Id,
            new UpdatePropertyRequest { Name = "New Home Name" });

        updated.Should().NotBeNull();
        updated!.UnitCount.Should().Be(1);
        _ctx.Db.Units.Single(u => u.PropertyId == created.Id).UnitNumber.Should().Be("New Home Name");
    }

    [Fact]
    public async Task UpdateAsync_DoesNotRenameManuallyNamedSingleUnit()
    {
        var property = SeedProperty("Standalone Home", PropertyType.SingleFamily);
        _ctx.Db.Units.Add(new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "Detached Garage Apartment",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            property.Id,
            new UpdatePropertyRequest { Name = "Renamed Home" });

        updated.Should().NotBeNull();
        _ctx.Db.Units.Single(u => u.PropertyId == property.Id)
            .UnitNumber.Should().Be("Detached Garage Apartment");
    }

    [Fact]
    public async Task UpdateAsync_ClearOwnerEntity_AllowsAssignedOwnerToBeDeleted()
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Owner To Clear",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();

        var property = SeedProperty("Owner Linked Property", PropertyType.MultiFamily);
        property.OwnerEntityId = owner.Id;
        _ctx.Db.SaveChanges();

        var updated = await _sut.UpdateAsync(
            PortfolioId,
            property.Id,
            new UpdatePropertyRequest { ClearOwnerEntity = true });

        updated.Should().NotBeNull();
        updated!.OwnerEntityId.Should().BeNull();
        _ctx.Db.Properties.Single(p => p.Id == property.Id).OwnerEntityId.Should().BeNull();

        var ownerService = new OwnerEntityService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
        (await ownerService.DeleteAsync(PortfolioId, owner.Id)).Should().BeTrue();
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
    public async Task DeleteAsync_SoftDeletesEmptyCanonicalUnitForPropertyUnitTypes()
    {
        var created = await _sut.CreateAsync(PortfolioId, NewProperty("Empty House", PropertyType.SingleFamily));
        created.Should().NotBeNull();
        var unit = _ctx.Db.Units.Single(u => u.PropertyId == created!.Id);

        var deleted = await _sut.DeleteAsync(PortfolioId, created!.Id);

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, created.Id)).Should().BeNull();
        _ctx.Db.Units.IgnoreQueryFilters().Single(u => u.Id == unit.Id).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenPropertyHasWorkOrderHistory()
    {
        var property = SeedPropertyWithUnit(out var unit);
        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = "Patch drywall",
            Description = "Repair hallway drywall",
            RequestedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        unit.DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, property.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("work order");
        ex.Which.Message.Should().Contain("history");
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenPropertyHasExpenseHistory()
    {
        var property = SeedPropertyWithUnit(out var unit);
        _ctx.Db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Description = "Paint",
            Amount = 125m,
            IncurredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        unit.DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, property.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("expense");
        ex.Which.Message.Should().Contain("history");
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenPropertyHasApplicationHistory()
    {
        var property = SeedPropertyWithUnit(out var unit);
        _ctx.Db.RentalApplications.Add(new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            FirstName = "Applied",
            LastName = "Tenant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        unit.DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();

        var act = async () => await _sut.DeleteAsync(PortfolioId, property.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("application");
        ex.Which.Message.Should().Contain("history");
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
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "101",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        return property;
    }

    private Property SeedProperty(string name, PropertyType type)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            PropertyType = type,
            AddressLine1 = $"{name} Street",
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

    private static CreatePropertyRequest NewProperty(string name, PropertyType type) => new()
    {
        Name = name,
        PropertyType = type,
        Status = PropertyStatus.Active,
        AddressLine1 = "293 Mallard Point Dr",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
    };

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
