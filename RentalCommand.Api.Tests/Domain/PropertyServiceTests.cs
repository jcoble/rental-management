using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
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
