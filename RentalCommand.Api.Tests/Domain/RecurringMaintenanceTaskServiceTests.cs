using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers portfolio-scoped CRUD for recurring-maintenance tasks: create validates the property
/// in-portfolio (and rejects out-of-portfolio refs), update/toggle/soft-delete behave, and the
/// active filter + soft-delete query filter are honored on reads.
/// </summary>
public class RecurringMaintenanceTaskServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly RecurringMaintenanceTaskService _sut;

    public RecurringMaintenanceTaskServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new RecurringMaintenanceTaskService(_ctx.Db, Mock.Of<IDataUpdateService>());
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsTask_WithNormalizedDate()
    {
        var property = SeedProperty();

        var result = await _sut.CreateAsync(PortfolioId, new CreateRecurringMaintenanceTaskRequest
        {
            PropertyId = property.Id,
            Title = "Quarterly gutter cleaning",
            Description = "Clear gutters and downspouts",
            Category = "Landscaping",
            RecurrenceInterval = RecurrenceInterval.Quarterly,
            NextDueDate = new DateTime(2026, 7, 1, 14, 30, 0, DateTimeKind.Utc),
            ScheduledTime = new TimeOnly(9, 15),
            EstimatedCost = 180m,
            Priority = WorkOrderPriority.Normal,
        });

        result.Should().NotBeNull();
        result!.Title.Should().Be("Quarterly gutter cleaning");
        result.RecurrenceInterval.Should().Be(RecurrenceInterval.Quarterly);
        result.ScheduledTime.Should().Be(new TimeOnly(9, 15));
        result.EstimatedCost.Should().Be(180m);
        result.MonthlyEstimatedCost.Should().Be(60m);
        result.IsActive.Should().BeTrue();
        // Time-of-day is dropped (it's a calendar date).
        result.NextDueDate.Date.Should().Be(new DateTime(2026, 7, 1));

        _ctx.Db.RecurringMaintenanceTasks.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_RejectsPropertyOutsidePortfolio()
    {
        // Property exists but belongs to a different portfolio → cross-tenant guard returns null.
        SeedPortfolio(999);
        var foreignProperty = SeedProperty(portfolioId: 999);

        var result = await _sut.CreateAsync(PortfolioId, new CreateRecurringMaintenanceTaskRequest
        {
            PropertyId = foreignProperty.Id,
            Title = "Should not be created",
            NextDueDate = DateTime.UtcNow.Date,
        });

        result.Should().BeNull();
        _ctx.Db.RecurringMaintenanceTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ChangesFields()
    {
        var property = SeedProperty();
        var task = SeedTask(property.Id);

        var result = await _sut.UpdateAsync(PortfolioId, task.Id, new UpdateRecurringMaintenanceTaskRequest
        {
            Title = "Renamed chore",
            RecurrenceInterval = RecurrenceInterval.SemiAnnually,
            Priority = WorkOrderPriority.High,
        });

        result.Should().NotBeNull();
        result!.Title.Should().Be("Renamed chore");
        result.RecurrenceInterval.Should().Be(RecurrenceInterval.SemiAnnually);
        result.Priority.Should().Be(WorkOrderPriority.High);
    }

    [Fact]
    public async Task SetActiveAsync_TogglesFlag()
    {
        var property = SeedProperty();
        var task = SeedTask(property.Id);

        var off = await _sut.SetActiveAsync(PortfolioId, task.Id, isActive: false);
        off!.IsActive.Should().BeFalse();

        var on = await _sut.SetActiveAsync(PortfolioId, task.Id, isActive: true);
        on!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_AndHidesFromReads()
    {
        var property = SeedProperty();
        var task = SeedTask(property.Id);

        var deleted = await _sut.DeleteAsync(PortfolioId, task.Id);
        deleted.Should().BeTrue();

        // Hidden from get + list (global query filter on DeletedAt).
        (await _sut.GetAsync(PortfolioId, task.Id)).Should().BeNull();
        (await _sut.ListAsync(PortfolioId, propertyId: null, activeOnly: null, new ListQuery())).Should().BeEmpty();

        // Row still present (soft, not hard, delete).
        _ctx.Db.RecurringMaintenanceTasks.IgnoreQueryFilters().Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_ActiveOnly_FiltersInactive()
    {
        var property = SeedProperty();
        SeedTask(property.Id, isActive: true, title: "Active one");
        SeedTask(property.Id, isActive: false, title: "Inactive one");

        var all = await _sut.ListAsync(PortfolioId, propertyId: null, activeOnly: null, new ListQuery());
        all.Should().HaveCount(2);

        var active = await _sut.ListAsync(PortfolioId, propertyId: null, activeOnly: true, new ListQuery());
        active.Should().HaveCount(1);
        active[0].Title.Should().Be("Active one");
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        var property = SeedProperty();
        var alpha = SeedTask(property.Id, title: "Alpha filters");
        var bravo = SeedTask(
            property.Id,
            title: "Bravo filters",
            interval: RecurrenceInterval.Weekly,
            scheduledTime: new TimeOnly(8, 30),
            estimatedCost: 120m);
        SeedGeneratedWorkOrder(bravo, "Bravo filters - June");
        SeedGeneratedWorkOrder(bravo, "Bravo filters - July");
        SeedTask(property.Id, title: "Cedar filters");
        SeedTask(property.Id, title: "Delta filters");

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, propertyId: null, activeOnly: null, new ListQuery
        {
            Sort = "title",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(t => t.Title).Should().Equal("Bravo filters", "Cedar filters");
        result.Items.Should().OnlyContain(t => t.PropertyName == "Test Property");
        result.Items[0].ScheduledTime.Should().Be(new TimeOnly(8, 30));
        result.Items[0].EstimatedCost.Should().Be(120m);
        result.Items[0].MonthlyEstimatedCost.Should().Be(520m);
        result.Items[0].GeneratedWorkOrderCount.Should().Be(2);
        result.Items[0].LastGeneratedWorkOrderId.Should().NotBeNull();
        alpha.Id.Should().BePositive();

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"RecurringMaintenanceTasks\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"WorkOrders\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("RecurringMaintenanceTaskId", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Helpers

    private void SeedPortfolio(int id)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = id,
            Name = $"Portfolio {id}",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private Property SeedProperty(int portfolioId = PortfolioId)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = "Test Property",
            AddressLine1 = "123 Main St",
            City = "Springfield",
            State = "IL",
            PostalCode = "62701",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private RecurringMaintenanceTask SeedTask(
        int propertyId,
        bool isActive = true,
        string title = "Recurring chore",
        RecurrenceInterval interval = RecurrenceInterval.Monthly,
        TimeOnly? scheduledTime = null,
        decimal? estimatedCost = null)
    {
        var now = DateTime.UtcNow;
        var task = new RecurringMaintenanceTask
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Title = title,
            RecurrenceInterval = interval,
            NextDueDate = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc),
            ScheduledTime = scheduledTime,
            EstimatedCost = estimatedCost,
            IsActive = isActive,
            Priority = WorkOrderPriority.Normal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringMaintenanceTasks.Add(task);
        _ctx.Db.SaveChanges();
        return task;
    }

    private WorkOrder SeedGeneratedWorkOrder(RecurringMaintenanceTask task, string title)
    {
        var now = DateTime.UtcNow;
        var workOrder = new WorkOrder
        {
            PortfolioId = task.PortfolioId,
            PropertyId = task.PropertyId,
            RecurringMaintenanceTaskId = task.Id,
            Title = title,
            Description = title,
            Category = task.Category ?? "General",
            Priority = task.Priority,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.WorkOrders.Add(workOrder);
        _ctx.Db.SaveChanges();
        return workOrder;
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
