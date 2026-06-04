using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// Covers the recurring-maintenance generator: a due active task produces exactly one work order
/// (with its initial status event) and advances NextDueDate by the interval; running twice in a
/// period does not double-generate; an inactive task generates nothing; the master config flag gates
/// the whole feature.
/// </summary>
public class RecurringMaintenanceServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public async Task DueActiveTask_GeneratesOneWorkOrder_AdvancesNextDueDate_AndIsIdempotent()
    {
        // Seed the due date a day in the past so the test is independent of the service's business
        // timezone (its local "today" can be a calendar day behind UtcNow.Date near midnight UTC).
        var dueDate = DateTime.UtcNow.Date.AddDays(-1);
        var property = SeedProperty();
        var task = SeedTask(
            property.Id,
            interval: RecurrenceInterval.Quarterly,
            nextDueDate: dueDate,
            isActive: true,
            title: "HVAC filter",
            category: "HVAC",
            priority: WorkOrderPriority.High);

        var sut = BuildService(enable: true);

        // First run creates exactly one work order.
        var firstResult = await sut.GenerateAsync();
        firstResult.Should().Be(1);

        var workOrders = _ctx.Db.WorkOrders.ToList();
        workOrders.Should().HaveCount(1);

        var wo = workOrders[0];
        wo.PortfolioId.Should().Be(PortfolioId);
        wo.PropertyId.Should().Be(property.Id);
        wo.Title.Should().Be("HVAC filter");
        wo.Category.Should().Be("HVAC");
        wo.Priority.Should().Be(WorkOrderPriority.High);
        wo.Status.Should().Be(WorkOrderStatus.New);

        // Initial status event written (null → New, System).
        var events = _ctx.Db.WorkOrderStatusEvents.ToList();
        events.Should().HaveCount(1);
        events[0].FromStatus.Should().BeNull();
        events[0].ToStatus.Should().Be(WorkOrderStatus.New);
        events[0].ChangedByLabel.Should().Be("System");

        // NextDueDate advanced by the interval (Quarterly = +3 months) off the due date; stamped.
        var reloaded = _ctx.Db.RecurringMaintenanceTasks.Single(t => t.Id == task.Id);
        reloaded.NextDueDate.Date.Should().Be(dueDate.AddMonths(3));
        reloaded.LastGeneratedAtUtc.Should().NotBeNull();

        // Second run in the same period creates nothing (NextDueDate is now in the future).
        var secondResult = await sut.GenerateAsync();
        secondResult.Should().Be(0);
        _ctx.Db.WorkOrders.Count().Should().Be(1);
    }

    [Fact]
    public async Task InactiveTask_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, interval: RecurrenceInterval.Monthly, nextDueDate: today, isActive: false);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task FutureDueTask_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, interval: RecurrenceInterval.Monthly, nextDueDate: today.AddDays(10), isActive: true);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task MasterFlagDisabled_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, interval: RecurrenceInterval.Monthly, nextDueDate: today, isActive: true);

        var sut = BuildService(enable: false);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task BackloggedTask_GeneratesOnlyOne_AndCatchesUpPastToday()
    {
        // A task that fell several weeks behind (Engine was down) should produce exactly ONE work
        // order this run and roll the schedule forward past "today" rather than flooding the queue.
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        var task = SeedTask(
            property.Id,
            interval: RecurrenceInterval.Weekly,
            nextDueDate: today.AddDays(-30),
            isActive: true);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        _ctx.Db.WorkOrders.Count().Should().Be(1);

        var reloaded = _ctx.Db.RecurringMaintenanceTasks.Single(t => t.Id == task.Id);
        reloaded.NextDueDate.Date.Should().BeAfter(today);
    }

    // -----------------------------------------------------------------------
    // Helpers

    private RecurringMaintenanceService BuildService(bool enable)
    {
        var cfg = new NotificationsConfig
        {
            EnableRecurringMaintenance = enable,
        };

        return new RecurringMaintenanceService(
            _ctx.Db,
            new FakeNotificationSettingsService(cfg),
            Mock.Of<IDataUpdateService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<RecurringMaintenanceService>.Instance);
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
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
        RecurrenceInterval interval,
        DateTime nextDueDate,
        bool isActive,
        string title = "Recurring chore",
        string? category = "General",
        WorkOrderPriority priority = WorkOrderPriority.Normal)
    {
        var now = DateTime.UtcNow;
        var task = new RecurringMaintenanceTask
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Title = title,
            Category = category,
            RecurrenceInterval = interval,
            NextDueDate = DateTime.SpecifyKind(nextDueDate.Date, DateTimeKind.Utc),
            IsActive = isActive,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringMaintenanceTasks.Add(task);
        _ctx.Db.SaveChanges();
        return task;
    }
}
