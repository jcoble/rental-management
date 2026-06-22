using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
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

        _sut = new ExpenseService(_db, new NoopDataUpdateService());
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
    public async Task UpdateAsync_WithLineItems_ReplacesAllExistingLineItemRows()
    {
        // Arrange — seed an expense with two typed line items.
        var now = DateTime.UtcNow;
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Category = ScheduleECategory.Repairs,
            Description = "Seed expense",
            Status = ExpenseStatus.Paid,
            Amount = 20.00m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        expense.LineItems.Add(new ExpenseLineItem { Description = "Old item 1", Amount = 10.00m, LineNumber = 1 });
        expense.LineItems.Add(new ExpenseLineItem { Description = "Old item 2", Amount = 10.00m, LineNumber = 2 });
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        var request = new UpdateExpenseRequest
        {
            LineItems =
            [
                new UpdateExpenseLineItem { Description = "New item A", Quantity = 2m, UnitPrice = 5.00m, Amount = 10.00m },
                new UpdateExpenseLineItem { Description = "New item B", Amount = 7.50m },
                new UpdateExpenseLineItem { Description = "New item C", Amount = 2.50m },
            ]
        };

        // Act
        var updated = await _sut.UpdateAsync(PortfolioId, expense.Id, request);

        // Assert — response carries the new rows.
        updated.Should().NotBeNull();
        updated!.LineItems.Should().HaveCount(3);
        updated.LineItems[0].Description.Should().Be("New item A");
        updated.LineItems[0].LineNumber.Should().Be(1);
        updated.LineItems[0].Quantity.Should().Be(2m);
        updated.LineItems[0].UnitPrice.Should().Be(5.00m);
        updated.LineItems[0].Amount.Should().Be(10.00m);
        updated.LineItems[1].Description.Should().Be("New item B");
        updated.LineItems[1].LineNumber.Should().Be(2);
        updated.LineItems[2].Description.Should().Be("New item C");
        updated.LineItems[2].LineNumber.Should().Be(3);

        // DB must have exactly 3 rows — old rows are gone.
        var dbItems = await _db.ExpenseLineItems.AsNoTracking()
            .Where(li => li.ExpenseId == expense.Id)
            .OrderBy(li => li.LineNumber)
            .ToListAsync();

        dbItems.Should().HaveCount(3);
        dbItems[0].Description.Should().Be("New item A");
        dbItems[1].Description.Should().Be("New item B");
        dbItems[2].Description.Should().Be("New item C");
    }

    [Fact]
    public async Task UpdateAsync_WithNullLineItems_LeavesExistingLineItemsUntouched()
    {
        // Arrange — seed an expense with one typed line item.
        var now = DateTime.UtcNow;
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Category = ScheduleECategory.Repairs,
            Description = "Seed expense",
            Status = ExpenseStatus.Paid,
            Amount = 10.00m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        expense.LineItems.Add(new ExpenseLineItem { Description = "Existing item", Amount = 10.00m, LineNumber = 1 });
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();

        // Act — update without providing LineItems (null = no change).
        var request = new UpdateExpenseRequest { Notes = "Just updating notes" };
        var updated = await _sut.UpdateAsync(PortfolioId, expense.Id, request);

        // Assert — the existing line item is still present.
        updated.Should().NotBeNull();
        updated!.LineItems.Should().HaveCount(1);
        updated.LineItems[0].Description.Should().Be("Existing item");

        var dbCount = await _db.ExpenseLineItems.AsNoTracking()
            .CountAsync(li => li.ExpenseId == expense.Id);
        dbCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_AllowsCorrectingReceiptFieldsAfterScanConfirm()
    {
        var expense = SeedExpense();
        var request = new UpdateExpenseRequest
        {
            Notes = "Corrected after reviewing the receipt again",
        };
        SetRequiredEditableReceiptProperty(request, "Subtotal", 40.00m);
        SetRequiredEditableReceiptProperty(request, "TaxAmount", 3.21m);
        SetRequiredEditableReceiptProperty(
            request,
            "ReceiptData",
            """{"receiptNumber":"R-200","lineItems":[{"description":"Washer hose","amount":40.00}]}""");

        var updated = await _sut.UpdateAsync(PortfolioId, expense.Id, request);

        updated.Should().NotBeNull();
        updated!.Subtotal.Should().Be(40.00m);
        updated.TaxAmount.Should().Be(3.21m);
        updated.ReceiptData.Should().Contain("R-200");

        var fromDb = await _db.Expenses.AsNoTracking().SingleAsync(e => e.Id == expense.Id);
        fromDb.Subtotal.Should().Be(40.00m);
        fromDb.TaxAmount.Should().Be(3.21m);
        fromDb.ReceiptData.Should().Contain("Washer hose");
    }

    [Fact]
    public async Task CreateAsync_FromScanDraft_PersistsTypedColumnsAndLineItems()
    {
        var now = DateTime.UtcNow;
        var request = new CreateExpenseRequest
        {
            Category    = ScheduleECategory.Repairs,
            Description = "ACME Hardware",
            Status      = ExpenseStatus.Paid,
            Amount      = 53.49m,
            Subtotal    = 49.99m,
            TaxAmount   = 3.50m,
            IncurredAt  = now,
            PaidAt      = now,
            ReceiptData = """{"receiptNumber":"R-42","lineItems":[{"description":"Washer hose","amount":12.99}]}""",
            // New typed columns promoted from the scan.
            PaymentMethod = "Visa",
            CardLast4     = "4242",
            DocumentKind  = "Receipt",
            LineItems =
            [
                new CreateExpenseLineItem { Description = "Washer hose", Quantity = 2m, UnitPrice = 6.50m, Amount = 12.99m, LineNumber = 1 },
                new CreateExpenseLineItem { Description = "Pipe tape",   Quantity = 1m, UnitPrice = 3.00m, Amount = 3.00m,  LineNumber = 2 },
            ],
        };

        var created = await _sut.CreateAsync(PortfolioId, request);

        created.Should().NotBeNull();
        created!.PaymentMethod.Should().Be("Visa");
        created.CardLast4.Should().Be("4242");
        created.DocumentKind.Should().Be("Receipt");
        created.ReceiptData.Should().Contain("R-42");

        // The typed columns + child line items are persisted to the DB (not just on the response).
        var fromDb = await _db.Expenses.AsNoTracking()
            .Include(e => e.LineItems)
            .SingleAsync(e => e.Id == created.Id);

        fromDb.PaymentMethod.Should().Be("Visa");
        fromDb.CardLast4.Should().Be("4242");
        fromDb.DocumentKind.Should().Be("Receipt");

        fromDb.LineItems.Should().HaveCount(2);
        var first = fromDb.LineItems.Single(li => li.LineNumber == 1);
        first.Description.Should().Be("Washer hose");
        first.Quantity.Should().Be(2m);
        first.UnitPrice.Should().Be(6.50m);
        first.Amount.Should().Be(12.99m);
        fromDb.LineItems.Single(li => li.LineNumber == 2).Description.Should().Be("Pipe tape");
    }

    private Expense SeedExpense()
    {
        var now = DateTime.UtcNow;
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            Category = ScheduleECategory.Repairs,
            Description = "Scanned receipt",
            Status = ExpenseStatus.Paid,
            Amount = 43.00m,
            IncurredAt = now,
            PaidAt = now,
            Subtotal = 39.00m,
            TaxAmount = 4.00m,
            ReceiptData = """{"receiptNumber":"OLD"}""",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Expenses.Add(expense);
        _db.SaveChanges();
        return expense;
    }

    private static void SetRequiredEditableReceiptProperty(UpdateExpenseRequest request, string name, object value)
    {
        var prop = typeof(UpdateExpenseRequest).GetProperty(name);
        prop.Should().NotBeNull($"{name} must be editable after a scan-confirmed expense is created");
        prop!.SetValue(request, value);
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

    private void SeedDirectUnitExpense(int unitId, string description, DateTime incurredAt, decimal amount)
    {
        var propertyId = _db.Units.Where(u => u.Id == unitId).Select(u => u.PropertyId).Single();
        _db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
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

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
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

internal sealed class ExpenseServiceTestDbContext : RentalCommandDbContext
{
    public ExpenseServiceTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
        modelBuilder.Entity<Payment>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<WorkOrder>().Property(e => e.ExtractedData).HasColumnType("TEXT");
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
