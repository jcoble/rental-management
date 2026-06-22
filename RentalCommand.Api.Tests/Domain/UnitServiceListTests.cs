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

public class UnitServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly UnitService _sut;

    public UnitServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListWithHealthPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedUnit("A", "Cedar Point Flats", openWorkOrders: 1);
        SeedUnit("B", "Cedar Point Flats", openWorkOrders: 3);
        SeedUnit("C", "Harbor View Apartments", openWorkOrders: 0);
        SeedUnit("D", "Harbor View Apartments", openWorkOrders: 2);

        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery
        {
            Sort = "-openWorkOrderCount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(u => u.UnitNumber).Should().Equal("D", "A");
        result.Items.Select(u => u.OpenWorkOrderCount).Should().Equal(2, 1);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Units\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedUnit(string unitNumber, string propertyName, int openWorkOrders)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            MarketRent = 1250m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

        for (var i = 0; i < openWorkOrders; i++)
        {
            _ctx.Db.WorkOrders.Add(new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                Title = $"{unitNumber} repair {i}",
                Description = "Open repair",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = $"{unitNumber} closed repair",
            Description = "Closed repair",
            Status = WorkOrderStatus.Completed,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
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
