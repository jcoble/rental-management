using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>Retains soft-delete coverage that still belongs to the canonical model.</summary>
public sealed class SoftDeleteKpiTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;

    public SoftDeleteKpiTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new AccountingServiceTestDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId, Name = "Test Portfolio", ManagementCompanyName = "Test Co",
            TimeZone = "UTC", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task SoftDeletedExpense_LineItemsAreFilteredOut()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId, Name = "General", AddressLine1 = "1 Main",
            City = "Columbus", State = "OH", PostalCode = "43219",
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        var expense = new Expense
        {
            PortfolioId = PortfolioId, PropertyId = property.Id,
            Category = ScheduleECategory.Repairs, Description = "Roof", Amount = 300m,
            IncurredAt = now, Status = ExpenseStatus.Pending, CreatedAt = now, UpdatedAt = now,
            LineItems = { new ExpenseLineItem { Description = "Shingles", Amount = 300m } },
        };
        _db.Expenses.Add(expense);
        _db.SaveChanges();

        (await _db.ExpenseLineItems.CountAsync()).Should().Be(1);
        expense.DeletedAt = now;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        (await _db.ExpenseLineItems.CountAsync()).Should().Be(0);
        (await _db.ExpenseLineItems.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }
}
