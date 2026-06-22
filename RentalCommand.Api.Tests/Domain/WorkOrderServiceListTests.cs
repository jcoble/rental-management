using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class WorkOrderServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly WorkOrderService _sut;

    public WorkOrderServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new WorkOrderService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_FiltersSortsAndPagesInSql()
    {
        SeedWorkOrder("A-100", "Cedar Point Flats", WorkOrderStatus.New, WorkOrderPriority.Normal);
        SeedWorkOrder("B-200", "Elm Ridge Homes", WorkOrderStatus.New, WorkOrderPriority.Normal);
        SeedWorkOrder("C-300", "Harbor View Apartments", WorkOrderStatus.New, WorkOrderPriority.Normal);
        SeedWorkOrder("D-400", "West Market Lofts", WorkOrderStatus.New, WorkOrderPriority.High);
        SeedWorkOrder("E-500", "York House", WorkOrderStatus.Completed, WorkOrderPriority.Normal);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new WorkOrderListQuery
        {
            Status = WorkOrderStatus.New,
            Priority = WorkOrderPriority.Normal,
            Sort = "propertyName",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(w => w.PropertyName).Should().Equal("Elm Ridge Homes", "Harbor View Apartments");
        result.Items.Should().OnlyContain(w => w.Status == WorkOrderStatus.New);
        result.Items.Should().OnlyContain(w => w.Priority == WorkOrderPriority.Normal);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"WorkOrders\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Properties", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedWorkOrder(string title, string propertyName, WorkOrderStatus status, WorkOrderPriority priority)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = new Property
            {
                PortfolioId = PortfolioId,
                Name = propertyName,
                AddressLine1 = "100 Test Street",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            },
            Title = title,
            Description = $"{title} repair",
            Category = "General",
            Status = status,
            Priority = priority,
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
