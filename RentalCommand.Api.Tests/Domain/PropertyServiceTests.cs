using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public class PropertyServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private PropertyService _sut = null!;
    private WorkspaceReadScope _scope;

    public PropertyServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
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

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedProperties("Alpha", "Bravo", "Charlie", "Delta", "Echo");

        await _ctx.ActivateApiScopeAsync(_scope);
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

    [Fact]
    public async Task ListAndDetail_UsePersistedRentalStructureForWorkspaceEntry()
    {
        var now = DateTime.UtcNow;
        var single = NewProperty("One address", RentalStructure.SingleRental, now);
        var duplex = NewProperty("One entered unit", RentalStructure.MultiRental, now);
        var singleUnit = NewUnit(single, "Rental", now);
        _ctx.Db.AddRange(single, duplex, singleUnit, NewUnit(duplex, "A", now));
        _ctx.Db.SaveChanges();

        await _ctx.ActivateApiScopeAsync(_scope);
        _commands.Clear();
        var page = await _sut.ListPageAsync(_scope, new PropertyListQuery
        {
            Sort = "name",
            Take = 20,
        });

        var singleRow = page.Items.Single(row => row.Id == single.Id);
        singleRow.WorkspaceEntry.Destination.Should().Be(PropertyWorkspaceDestination.Unit);
        singleRow.WorkspaceEntry.UnitId.Should().Be(singleUnit.Id);
        singleRow.WorkspaceEntry.Areas.Should().BeEmpty();

        var duplexRow = page.Items.Single(row => row.Id == duplex.Id);
        duplexRow.UnitCount.Should().Be(1);
        duplexRow.WorkspaceEntry.Destination.Should().Be(PropertyWorkspaceDestination.Property);
        duplexRow.WorkspaceEntry.UnitId.Should().BeNull();
        duplexRow.WorkspaceEntry.Areas.Should().Equal(
            PropertyWorkspaceArea.Summary,
            PropertyWorkspaceArea.Rentals,
            PropertyWorkspaceArea.OwnershipManagement,
            PropertyWorkspaceArea.PropertyWork,
            PropertyWorkspaceArea.PropertyFinances,
            PropertyWorkspaceArea.DocumentsHistory);

        var listCommands = _commands.ToArray();
        listCommands.Should().HaveCount(2);
        listCommands[1].Should().Contain("RentalStructure");
        listCommands[1].Should().Contain("Units");

        _commands.Clear();
        var detail = await _sut.GetAsync(_scope, single.Id);
        detail.Should().NotBeNull();
        detail!.WorkspaceEntry.UnitId.Should().Be(singleUnit.Id);
        _commands.Should().ContainSingle();
    }

    [Fact]
    public async Task ListAndDetail_ProjectOnlyCurrentEffectiveOwnershipRelationshipFacts()
    {
        var now = DateTime.UtcNow;
        var property = NewProperty("Shared ownership", RentalStructure.MultiRental, now);
        var currentOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Current Owner LLC",
            Email = "current-owner@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var formerOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Former Owner LLC",
            Email = "former-owner@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        property.Ownerships.Add(new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            OwnerEntity = currentOwner,
            OwnershipSharePercent = 62.5000m,
            EffectiveFromUtc = now.AddDays(-10),
            StatementRecipientName = "Current Statements",
            StatementRecipientEmail = "statements@example.test",
            PayeeName = "Current Payee LLC",
        });
        property.Ownerships.Add(new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            OwnerEntity = formerOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = now.AddYears(-1),
            EffectiveToUtc = now.AddDays(-30),
            StatementRecipientName = "Former Statements",
            StatementRecipientEmail = "former-statements@example.test",
            PayeeName = "Former Payee LLC",
        });
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();

        await _ctx.ActivateApiScopeAsync(_scope);
        _commands.Clear();
        var page = await _sut.ListPageAsync(_scope, new PropertyListQuery
        {
            Search = "Current Owner",
            Sort = "name",
            Take = 20,
        });

        var row = page.Items.Should().ContainSingle().Subject;
        var ownership = row.Ownerships.Should().ContainSingle().Subject;
        ownership.OwnerEntityId.Should().Be(currentOwner.Id);
        ownership.OwnerName.Should().Be("Current Owner LLC");
        ownership.OwnershipSharePercent.Should().Be(62.5000m);
        ownership.StatementRecipientName.Should().Be("Current Statements");
        ownership.StatementRecipientEmail.Should().Be("statements@example.test");
        ownership.PayeeName.Should().Be("Current Payee LLC");
        _commands.Should().HaveCount(2, "the translated count and bounded page are the only reads");
        var countSql = _commands.Single(sql =>
            sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        var pageSql = _commands.Single(sql =>
            !sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        new[] { countSql, pageSql }.Should().OnlyContain(sql =>
            sql.Contains("public.rc_api_effective_capability_scopes", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("PropertyOwnerships", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EffectiveFromUtc", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EffectiveToUtc", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var formerOwnerPage = await _sut.ListPageAsync(_scope, new PropertyListQuery
        {
            Search = "Former Owner",
            Sort = "name",
            Take = 20,
        });

        formerOwnerPage.Items.Should().BeEmpty(
            "expired ownership relationships must not contribute owner-name search matches");
        formerOwnerPage.TotalCount.Should().Be(0);
        _commands.Should().HaveCount(2, "the negative owner search remains a count and bounded page read");
        var formerCountSql = _commands.Single(sql =>
            sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        var formerPageSql = _commands.Single(sql =>
            !sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        new[] { formerCountSql, formerPageSql }.Should().OnlyContain(sql =>
            sql.Contains("public.rc_api_effective_capability_scopes", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("PropertyOwnerships", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EffectiveFromUtc", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EffectiveToUtc", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var detail = await _sut.GetAsync(_scope, property.Id);

        detail.Should().NotBeNull();
        detail!.Ownerships.Should().ContainSingle(item =>
            item.OwnerEntityId == currentOwner.Id
            && item.OwnershipSharePercent == 62.5000m
            && item.StatementRecipientName == "Current Statements"
            && item.StatementRecipientEmail == "statements@example.test"
            && item.PayeeName == "Current Payee LLC");
        detail.Ownerships.Should().NotContain(item => item.OwnerEntityId == formerOwner.Id);
        _commands.Should().ContainSingle();
        _commands[0].Should().Contain("PropertyOwnerships");
    }

    [Fact]
    public async Task PropertyGetAndList_ProjectTypeAndUnitAggregates()
    {
        var now = DateTime.UtcNow;
        var property = NewProperty("Willow Run", RentalStructure.MultiRental, now);
        property.PropertyType = PropertyType.MultiFamily;
        var occupiedA = NewUnit(property, "A", now);
        var occupiedB = NewUnit(property, "B", now);
        var vacant = NewUnit(property, "C", now);
        _ctx.Db.AddRange(property, occupiedA, occupiedB, vacant);
        _ctx.Db.SaveChanges();
        SeedCurrentPossession(property, occupiedA, now);
        SeedCurrentPossession(property, occupiedB, now);

        await _ctx.ActivateApiScopeAsync(_scope);
        var detail = await _sut.GetAsync(_scope, property.Id);
        detail.Should().NotBeNull();
        detail!.PropertyType.Should().Be(PropertyType.MultiFamily);
        detail.UnitCount.Should().Be(3);
        detail.OccupiedUnits.Should().Be(2);

        var list = await _sut.ListAsync(_scope, new ListQuery());
        var row = list.Single(item => item.Id == property.Id);
        row.UnitCount.Should().Be(3);
        row.OccupiedUnits.Should().Be(2);
    }

    private void SeedCurrentPossession(Property property, Unit unit, DateTime now)
    {
        _ctx.Db.LeaseManagements.Add(new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"OCC-{unit.Id}",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
            CreatedAtUtc = now,
            CreatedByUserId = _scope.UserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        });
        _ctx.Db.SaveChanges();
    }

    private static Property NewProperty(string name, RentalStructure structure, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Name = name,
        RentalStructure = structure,
        AddressLine1 = $"{name} Street",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Unit NewUnit(Property property, string unitNumber, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Property = property,
        UnitNumber = unitNumber,
        CreatedAt = now,
        UpdatedAt = now,
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
