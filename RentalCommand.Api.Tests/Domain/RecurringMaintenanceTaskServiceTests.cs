using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the receipt-backed mutation contract and verifies recurring-maintenance list work stays
/// translated to database count, projection, sorting, and paging queries.
/// </summary>
public class RecurringMaintenanceTaskServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly RecurringMaintenanceTaskService _sut;
    private readonly WorkspaceReadScope _scope;

    public RecurringMaintenanceTaskServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(RecurringMaintenanceTaskServiceTests));
        _sut = new RecurringMaintenanceTaskService(_ctx.Db, TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    // -----------------------------------------------------------------------

    [Fact]
    public void MutationsExposeOnlyScopedReceiptBackedOverloads()
    {
        var mutationMethods = typeof(IRecurringMaintenanceTaskService).GetMethods()
            .Where(method => method.Name is "CreateAuthorizedAsync" or "UpdateAuthorizedAsync"
                or "SetActiveAuthorizedAsync" or "DeleteAuthorizedAsync")
            .ToArray();

        mutationMethods.Should().HaveCount(4);
        mutationMethods.Should().OnlyContain(method =>
            method.GetParameters().First().ParameterType ==
                typeof(RentalCommand.Core.Authorization.WorkspaceReadScope)
            && method.GetParameters().Any(parameter => parameter.Name == "idempotencyKey"));
    }

    [Fact]
    public async Task ListAsync_ActiveOnly_FiltersInactive()
    {
        var property = SeedProperty();
        SeedTask(property.Id, isActive: true, title: "Active one");
        SeedTask(property.Id, isActive: false, title: "Inactive one");

        var all = await _sut.ListAuthorizedAsync(_scope, propertyId: null, activeOnly: null, new ListQuery());
        all.Should().HaveCount(2);

        var active = await _sut.ListAuthorizedAsync(_scope, propertyId: null, activeOnly: true, new ListQuery());
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
        var result = await _sut.ListPageAuthorizedAsync(_scope, propertyId: null, activeOnly: null, new ListQuery
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

    [Fact]
    public async Task ListPageAsync_UnitQueryFiltersSortsAndPagesInTwoSqlCommands()
    {
        var property = SeedProperty();
        var firstUnit = SeedUnit(property.Id, "1A");
        var secondUnit = SeedUnit(property.Id, "1B");
        SeedTask(property.Id, title: "Alpha First Unit", unitId: firstUnit.Id);
        SeedTask(property.Id, title: "Bravo First Unit", unitId: firstUnit.Id);
        SeedTask(property.Id, title: "Aardvark Second Unit", unitId: secondUnit.Id);

        _commands.Clear();
        var result = await _sut.ListPageAuthorizedAsync(
            _scope,
            propertyId: null,
            activeOnly: null,
            new RecurringMaintenanceTaskListQuery
            {
                UnitId = firstUnit.Id,
                Sort = "title",
                Skip = 1,
                Take = 1,
            });

        result.TotalCount.Should().Be(2);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(1);
        result.Items.Should().ContainSingle(item =>
            item.UnitId == firstUnit.Id && item.Title == "Bravo First Unit");
        var listCommands = _commands.Where(sql =>
            sql.Contains("FROM \"RecurringMaintenanceTasks\"", StringComparison.OrdinalIgnoreCase)).ToList();
        listCommands.Should().HaveCount(2);
        listCommands.Should().OnlyContain(sql =>
            sql.Contains("UnitId", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("active-toggle")]
    public async Task ManualChange_ClearsQuarantine_AndMakesTaskClaimableAgain(string operation)
    {
        var property = SeedProperty();
        var task = SeedTask(property.Id, title: "Quarantined schedule");
        task.WorkerClaimAttemptCount = 3;
        task.WorkerClaimLastFailureReason = "Internal failure detail";
        task.WorkerClaimLastFailureAtUtc = DateTime.UtcNow.AddMinutes(-2);
        task.WorkerClaimQuarantinedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        _ctx.Db.SaveChanges();

        using var services = AtomicDomainTestKernel.CreateForRecurringMaintenance(_ctx.ConnectionString);
        using var serviceScope = services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<RecurringMaintenanceTaskService>();

        var response = operation switch
        {
            "update" => await service.UpdateAuthorizedAsync(
                _scope,
                task.Id,
                new UpdateRecurringMaintenanceTaskRequest
                {
                    Title = "Recovered schedule",
                    IsActive = true,
                },
                "h5-quarantine-clear-update"),
            "active-toggle" => await service.SetActiveAuthorizedAsync(
                _scope,
                task.Id,
                true,
                "h5-quarantine-clear-active"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        response.Should().NotBeNull();
        response!.AutomationFailureAttemptCount.Should().Be(0);
        response.AutomationFailureReason.Should().BeNull();
        response.AutomationFailureAtUtc.Should().BeNull();
        response.AutomationQuarantinedAtUtc.Should().BeNull();

        _ctx.Db.ChangeTracker.Clear();
        var persisted = _ctx.Db.RecurringMaintenanceTasks.Single(row => row.Id == task.Id);
        persisted.WorkerClaimAttemptCount.Should().Be(0);
        persisted.WorkerClaimLastFailureReason.Should().BeNull();
        persisted.WorkerClaimLastFailureAtUtc.Should().BeNull();
        persisted.WorkerClaimQuarantinedAtUtc.Should().BeNull();

        var claims = await new TestScheduledAutomationClaimStore(_ctx.Db)
            .ClaimRecurringMaintenanceAsync(
                "h5-manual-recovery",
                DateTime.UtcNow.Date.AddDays(1),
                TimeSpan.FromMinutes(5),
                batchSize: 25);
        claims.Should().ContainSingle(claim => claim.Id == task.Id);
    }

    // -----------------------------------------------------------------------
    // Helpers

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
        decimal? estimatedCost = null,
        int? unitId = null)
    {
        var now = DateTime.UtcNow;
        var task = new RecurringMaintenanceTask
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
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

    private Unit SeedUnit(int propertyId, string unitNumber)
    {
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitNumber = unitNumber,
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();
        return unit;
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
