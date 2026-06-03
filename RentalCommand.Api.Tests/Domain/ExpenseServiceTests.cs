using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    public ExpenseServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
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

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
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
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
