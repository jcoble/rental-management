using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public class ExpenseServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly ExpenseService _sut;
    private readonly List<string> _commands = [];

    public ExpenseServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new ExpenseServiceTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new ExpenseService(
            _db, Mock.Of<IFileStorage>(), TimeProvider.System, Mock.Of<IRequestWriteExecutor>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ListPageAsync_FiltersWorkOrderReceiptsAndPagesInSql()
    {
        var (unitId, otherUnitId) = SeedUnitsForListPage();
        SeedWorkOrderExpense(unitId, "Alpha", DateTime.UtcNow.AddDays(-4), 10m);
        SeedWorkOrderExpense(unitId, "Bravo", DateTime.UtcNow.AddDays(-3), 20m);
        SeedWorkOrderExpense(unitId, "Cedar", DateTime.UtcNow.AddDays(-2), 30m);
        SeedDirectUnitExpense(unitId, "Direct unit", DateTime.UtcNow.AddDays(-1), 40m);
        SeedWorkOrderExpense(otherUnitId, "Other unit", DateTime.UtcNow, 50m);

        _commands.Clear();
        var page = await _sut.ListPageAsync(
            PortfolioId,
            propertyId: null,
            unitId,
            workOrderId: null,
            workOrderLinkedOnly: true,
            new ListQuery
            {
                Sort = "description",
                Skip = 1,
                Take = 2,
            });

        page.TotalCount.Should().Be(3);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(e => e.Description).Should().Equal("Bravo", "Cedar");
        page.Items.Should().OnlyContain(e => e.WorkOrderId != null);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Expenses\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_FiltersIncurredDateWindowInSql()
    {
        var (unitId, _) = SeedUnitsForListPage();
        var day1 = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedDirectUnitExpense(unitId, "Feb 1 supply", day1, 10m);
        SeedDirectUnitExpense(unitId, "Feb 2 supply", day1.AddDays(1), 20m);
        SeedDirectUnitExpense(unitId, "Feb 3 supply", day1.AddDays(2), 30m);
        SeedDirectUnitExpense(unitId, "Feb 4 supply", day1.AddDays(3), 40m);
        SeedDirectUnitExpense(unitId, "Feb 5 supply", day1.AddDays(4), 50m);

        _commands.Clear();
        var page = await _sut.ListPageAsync(
            PortfolioId,
            propertyId: null,
            unitId: null,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ExpenseListQuery
            {
                IncurredFrom = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc),
                IncurredTo = new DateTime(2026, 2, 4, 0, 0, 0, DateTimeKind.Utc),
                Sort = "incurredAt",
                Take = 10,
            });

        page.TotalCount.Should().Be(3);
        page.Items.Select(e => e.Description).Should().Equal("Feb 2 supply", "Feb 3 supply", "Feb 4 supply");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("IncurredAt", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("IncurredAt", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_SortsExpenseDateColumnsInSql()
    {
        var (unitId, _) = SeedUnitsForListPage();
        var day1 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        SeedDirectUnitExpense(unitId, "Paid Mar 1", day1, 10m, dueDate: day1.AddDays(10), paidAt: day1);
        SeedDirectUnitExpense(unitId, "Paid Mar 3", day1, 20m, dueDate: day1.AddDays(8), paidAt: day1.AddDays(2));
        SeedDirectUnitExpense(unitId, "Paid Mar 2", day1, 30m, dueDate: day1.AddDays(9), paidAt: day1.AddDays(1));

        _commands.Clear();
        var paidPage = await _sut.ListPageAsync(
            PortfolioId,
            propertyId: null,
            unitId: null,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ListQuery
            {
                Sort = "paidAt",
                Take = 10,
            });

        paidPage.Items.Select(e => e.Description).Should().Equal("Paid Mar 1", "Paid Mar 2", "Paid Mar 3");
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("PaidAt", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var duePage = await _sut.ListPageAsync(
            PortfolioId,
            propertyId: null,
            unitId: null,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ListQuery
            {
                Sort = "-dueDate",
                Take = 10,
            });

        duePage.Items.Select(e => e.Description).Should().Equal("Paid Mar 1", "Paid Mar 2", "Paid Mar 3");
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("DueDate", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_OrdersReceiptLineItemsInSql()
    {
        var now = DateTime.UtcNow;
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Category = ScheduleECategory.Repairs,
            Description = "Receipt with line items",
            Status = ExpenseStatus.Paid,
            Amount = 30m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        expense.LineItems.Add(new ExpenseLineItem { Description = "Second", Amount = 20m, LineNumber = 2 });
        expense.LineItems.Add(new ExpenseLineItem { Description = "First", Amount = 10m, LineNumber = 1 });
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        _commands.Clear();

        var result = await _sut.GetAsync(PortfolioId, expense.Id);

        result.Should().NotBeNull();
        result!.LineItems.Select(li => li.Description).Should().Equal("First", "Second");

        var lineItemsSql = _commands.FirstOrDefault(sql =>
            sql.Contains("ExpenseLineItems", StringComparison.OrdinalIgnoreCase));
        lineItemsSql.Should().NotBeNull("the expense detail query must load receipt line items from SQL");
        lineItemsSql!.Should().Contain("ORDER BY");
        lineItemsSql.Should().MatchRegex("ORDER BY[\\s\\S]*LineNumber");
    }

    [Fact]
    public async Task ListPageAsync_FiltersSortsAndProjectsAllocationsInSqlWithoutDuplicates()
    {
        var (unitId, otherUnitId) = SeedUnitsForListPage();
        SeedDirectUnitExpense(unitId, "Allocated 20", DateTime.UtcNow.AddDays(-2), 20m);
        SeedDirectUnitExpense(unitId, "Allocated 40", DateTime.UtcNow.AddDays(-1), 40m);
        SeedDirectUnitExpense(otherUnitId, "Decoy", DateTime.UtcNow, 80m);
        var selected = _db.Expenses
            .Where(expense => expense.UnitId == unitId)
            .OrderBy(expense => expense.Amount)
            .ToArray();
        _db.ExpenseAllocations.AddRange(
            new ExpenseAllocation
            {
                PortfolioId = PortfolioId,
                ExpenseId = selected[0].Id,
                TargetKind = ExpenseAllocationTargetKind.Unit,
                UnitId = unitId,
                Amount = 20m,
                CreatedAt = DateTime.UtcNow,
            },
            new ExpenseAllocation
            {
                PortfolioId = PortfolioId,
                ExpenseId = selected[1].Id,
                TargetKind = ExpenseAllocationTargetKind.Unit,
                UnitId = unitId,
                Amount = 40m,
                CreatedAt = DateTime.UtcNow,
            });
        _db.SaveChanges();

        _commands.Clear();
        var page = await _sut.ListPageAsync(
            PortfolioId,
            propertyId: null,
            unitId: null,
            workOrderId: null,
            workOrderLinkedOnly: false,
            new ExpenseListQuery
            {
                AllocationTargetKind = ExpenseAllocationTargetKind.Unit,
                AllocationTargetId = unitId,
                Sort = "-allocationTotal",
                Skip = 0,
                Take = 10,
            });

        page.TotalCount.Should().Be(2);
        page.Items.Select(item => item.Id).Should().OnlyHaveUniqueItems();
        page.Items.Select(item => item.AllocationTotal).Should().Equal(40m, 20m);
        _commands.Should().HaveCount(2,
            "one translated count and one bounded page query are the list budget");
        _commands[0].Should().Contain("ExpenseAllocations");
        _commands[0].Should().Contain("EXISTS");
        _commands[1].Should().Contain("ExpenseAllocations");
        (_commands[1].Contains("SUM(", StringComparison.OrdinalIgnoreCase) ||
         _commands[1].Contains("ef_sum(", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("allocation totals must be summed in translated SQL");
        _commands[1].Should().Contain("ORDER BY");
        _commands[1].Should().Contain("LIMIT");
    }

    [Fact]
    public async Task GetAsync_ProjectsCanonicalScopeAndTypedAllocationsInSql()
    {
        var (unitId, _) = SeedUnitsForListPage();
        SeedDirectUnitExpense(unitId, "Canonical allocation", DateTime.UtcNow, 25m);
        var expense = await _db.Expenses.SingleAsync(row => row.Description == "Canonical allocation");
        _db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = PortfolioId,
            ExpenseId = expense.Id,
            TargetKind = ExpenseAllocationTargetKind.Unit,
            UnitId = unitId,
            Amount = 25m,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        _commands.Clear();
        var result = await _sut.GetAsync(PortfolioId, expense.Id);

        result.Should().NotBeNull();
        result!.OperationalScope.Should().Be(ExpenseOperationalScope.Unit);
        result.AllocationTotal.Should().Be(25m);
        result.Allocations.Should().ContainSingle().Which.UnitId.Should().Be(unitId);
        _commands.Should().Contain(sql =>
            sql.Contains("ExpenseAllocations", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MutationsExposeOnlyScopedReceiptBackedOverloads()
    {
        var mutationMethods = typeof(IExpenseService).GetMethods()
            .Where(method => method.Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync")
            .ToArray();

        mutationMethods.Should().HaveCount(3);
        mutationMethods.Should().OnlyContain(method =>
            method.GetParameters().First().ParameterType == typeof(RentalCommand.Core.Authorization.WorkspaceReadScope) &&
            method.GetParameters().Any(parameter => parameter.Name == "idempotencyKey"));
    }

    [Fact]
    public async Task MutationsPassTheAmbientBusinessClockIntoEveryAtomicCommand()
    {
        var businessNowUtc = new DateTime(2027, 1, 31, 5, 0, 0, DateTimeKind.Utc);
        var captured = new List<AtomicMoneyMutationCommand>();
        var writes = new Mock<IRequestWriteExecutor>(MockBehavior.Strict);
        writes.Setup(service => service.ExecuteExactAsync<
                AtomicMoneyMutationCommand, AtomicMoneyMutationResult>(
                It.IsAny<string>(),
                It.IsAny<TransactionalWrite<AtomicMoneyMutationCommand, AtomicMoneyMutationResult>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string,
                TransactionalWrite<AtomicMoneyMutationCommand, AtomicMoneyMutationResult>,
                CancellationToken>((_, write, _) => captured.Add(write.Request))
            .ReturnsAsync(new AtomicCommandOutcome<AtomicMoneyMutationResult>(
                new AtomicMoneyMutationResult(false, false, 0),
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()));
        var service = new ExpenseService(
            _db,
            Mock.Of<IFileStorage>(),
            new FixedTimeProvider(new DateTimeOffset(businessNowUtc)),
            writes.Object);
        var scope = new WorkspaceReadScope(
            PortfolioId, 2, Guid.Parse("7dc8658f-a9d8-4760-b59c-f3087f06ee40"), 3, 4);

        await service.CreateAsync(scope, new CreateExpenseRequest(), "clock-create");
        await service.UpdateAsync(scope, 41, new UpdateExpenseRequest(), "clock-update");
        await service.DeleteAsync(scope, 41, "clock-delete");

        captured.Select(command => command.Operation).Should().Equal(
            AtomicMoneyOperation.Create,
            AtomicMoneyOperation.Update,
            AtomicMoneyOperation.Delete);
        captured.Should().OnlyContain(command => command.BusinessNowUtc == businessNowUtc);
    }

    [Fact]
    public async Task AtomicMoneyMutationRejectsAMissingBusinessClockBeforeStartingAnAttempt()
    {
        var command = new AtomicMoneyMutationCommand(
            PortfolioId,
            2,
            Guid.Parse("62ebbe35-bf31-4590-8b95-04c146281fa1"),
            3,
            4,
            CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense,
            AtomicMoneyOperation.Create,
            0,
            "missing-clock",
            "{}",
            default);
        var handler = new AtomicMoneyMutationHandler(_db);

        var act = () => handler.ExecuteAsync(
            command, Mock.Of<IAtomicCommandContext>(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*action, and time are required.*");
    }

    private (int UnitId, int OtherUnitId) SeedUnitsForListPage()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Cedar Point Flats",
            AddressLine1 = "100 Cedar St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "1A",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherUnit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "2A",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Units.AddRange(unit, otherUnit);
        _db.SaveChanges();

        return (unit.Id, otherUnit.Id);
    }

    private void SeedWorkOrderExpense(int unitId, string description, DateTime incurredAt, decimal amount)
    {
        var now = DateTime.UtcNow;
        var propertyId = _db.Units.Where(u => u.Id == unitId).Select(u => u.PropertyId).Single();
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            Title = $"{description} work order",
            Description = $"{description} work",
            Category = "General",
            Status = WorkOrderStatus.New,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
            UpdatedAt = now,
        };
        _db.WorkOrders.Add(workOrder);
        _db.SaveChanges();

        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.WorkOrder,
            PropertyId = propertyId,
            UnitId = unitId,
            WorkOrderId = workOrder.Id,
            Category = ScheduleECategory.Repairs,
            Description = description,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = incurredAt,
            CreatedAt = incurredAt,
            UpdatedAt = incurredAt,
        });
        _db.SaveChanges();
    }

    private void SeedDirectUnitExpense(
        int unitId,
        string description,
        DateTime incurredAt,
        decimal amount,
        DateTime? dueDate = null,
        DateTime? paidAt = null)
    {
        var propertyId = _db.Units.Where(u => u.Id == unitId).Select(u => u.PropertyId).Single();
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.Unit,
            PropertyId = propertyId,
            UnitId = unitId,
            Category = ScheduleECategory.Repairs,
            Description = description,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = incurredAt,
            DueDate = dueDate,
            PaidAt = paidAt,
            CreatedAt = incurredAt,
            UpdatedAt = incurredAt,
        });
        _db.SaveChanges();
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

internal sealed class ExpenseServiceTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
{
    public ExpenseServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
