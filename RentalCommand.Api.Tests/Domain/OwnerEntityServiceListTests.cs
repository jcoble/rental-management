using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class OwnerEntityServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly OwnerEntityService _sut;

    public OwnerEntityServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new OwnerEntityService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedOwner("Cedar Owner", OwnerEntityType.Person);
        SeedOwner("Delta Holdings", OwnerEntityType.LLC);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new ListQuery
        {
            Sort = "name",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(o => o.Name).Should().Equal("Bravo Trust", "Cedar Owner");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"OwnerEntities\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_ReturnsAssignedPropertyCountsDbSide()
    {
        var owner = SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        var otherOwner = SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedProperty(owner, "Alpha One");
        SeedProperty(owner, "Alpha Two");
        SeedProperty(otherOwner, "Bravo One");

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new ListQuery
        {
            Sort = "name",
            Take = 10,
        });

        result.Items.Single(o => o.Id == owner.Id).AssignedPropertyCount.Should().Be(2);
        result.Items.Single(o => o.Id == otherOwner.Id).AssignedPropertyCount.Should().Be(1);
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_FiltersByOwnerEntityTypeDbSide()
    {
        SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedOwner("Cedar Owner", OwnerEntityType.Person);
        SeedOwner("Delta Holdings", OwnerEntityType.LLC);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new OwnerEntityListQuery
        {
            OwnerEntityType = OwnerEntityType.LLC,
            Sort = "name",
            Take = 10,
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(o => o.Name).Should().Equal("Alpha Holdings", "Delta Holdings");
        _commands.Should().Contain(sql =>
            sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OwnerEntityType", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_ReturnsAssignedPropertyCount()
    {
        var owner = SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedProperty(owner, "Alpha One");
        SeedProperty(owner, "Alpha Two");

        var result = await _sut.GetAsync(PortfolioId, owner.Id);

        result.Should().NotBeNull();
        result!.AssignedPropertyCount.Should().Be(2);
    }

    private OwnerEntity SeedOwner(string name, OwnerEntityType type)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = name,
            OwnerEntityType = type,
            Email = $"{name.Replace(" ", ".", StringComparison.Ordinal).ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private void SeedProperty(OwnerEntity owner, string name)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Properties.Add(new Property
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = owner.Id,
            Name = name,
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        });
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
