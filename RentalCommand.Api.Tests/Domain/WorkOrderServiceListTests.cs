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
            NullLogger<WorkOrderService>.Instance,
            TimeProvider.System);
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

    [Fact]
    public async Task ListPageAsync_OpenOnlyFiltersClosedStatusesInSql()
    {
        SeedWorkOrder("Open new", "Cedar Point Flats", WorkOrderStatus.New, WorkOrderPriority.Normal);
        SeedWorkOrder("Open scheduled", "Elm Ridge Homes", WorkOrderStatus.Scheduled, WorkOrderPriority.Normal);
        SeedWorkOrder("Closed complete", "Harbor View Apartments", WorkOrderStatus.Completed, WorkOrderPriority.Normal);
        SeedWorkOrder("Closed cancelled", "West Market Lofts", WorkOrderStatus.Cancelled, WorkOrderPriority.Normal);
        SeedWorkOrder("Closed archived", "York House", WorkOrderStatus.Archived, WorkOrderPriority.Normal);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new WorkOrderListQuery
        {
            OpenOnly = true,
            Sort = "title",
            Take = 10,
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(w => w.Title).Should().Equal("Open new", "Open scheduled");
        result.Items.Should().OnlyContain(w =>
            w.Status != WorkOrderStatus.Completed &&
            w.Status != WorkOrderStatus.Cancelled &&
            w.Status != WorkOrderStatus.Archived);

        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"WorkOrders\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("NOT IN", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_FiltersRequestedDateWindowInSql()
    {
        var day1 = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedWorkOrder("April 1", "Cedar Point Flats", WorkOrderStatus.New, WorkOrderPriority.Normal, day1);
        SeedWorkOrder("April 2", "Elm Ridge Homes", WorkOrderStatus.New, WorkOrderPriority.Normal, day1.AddDays(1));
        SeedWorkOrder("April 3", "Harbor View Apartments", WorkOrderStatus.New, WorkOrderPriority.Normal, day1.AddDays(2));
        SeedWorkOrder("April 4", "West Market Lofts", WorkOrderStatus.New, WorkOrderPriority.Normal, day1.AddDays(3));

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new WorkOrderListQuery
        {
            RequestedFrom = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc),
            RequestedTo = new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc),
            Sort = "requestedAt",
            Take = 10,
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(w => w.Title).Should().Equal("April 2", "April 3");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("RequestedAt", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("RequestedAt", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_SortsCompletedDateInSql()
    {
        var day1 = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedWorkOrder("Completed first", "Cedar Point Flats", WorkOrderStatus.Completed, WorkOrderPriority.Normal, day1, day1);
        SeedWorkOrder("Completed third", "Elm Ridge Homes", WorkOrderStatus.Completed, WorkOrderPriority.Normal, day1, day1.AddDays(2));
        SeedWorkOrder("Completed second", "Harbor View Apartments", WorkOrderStatus.Completed, WorkOrderPriority.Normal, day1, day1.AddDays(1));

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new WorkOrderListQuery
        {
            Sort = "-completedAt",
            Take = 10,
        });

        result.Items.Select(w => w.Title).Should().Equal("Completed third", "Completed second", "Completed first");

        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("CompletedAt", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedWorkOrder(
        string title,
        string propertyName,
        WorkOrderStatus status,
        WorkOrderPriority priority,
        DateTime? requestedAt = null,
        DateTime? completedAt = null)
    {
        var now = requestedAt ?? DateTime.UtcNow;
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
            CompletedAt = completedAt,
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
