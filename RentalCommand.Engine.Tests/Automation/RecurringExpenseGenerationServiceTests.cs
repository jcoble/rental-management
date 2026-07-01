using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// Recurring expenses must materialize once per period (idempotent, spec §8) and catch up missed
/// periods so deductions are never understated. These tests pin that behavior against the SQLite
/// test context.
/// </summary>
public class RecurringExpenseGenerationServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx;

    public RecurringExpenseGenerationServiceTests() => _ctx = new SqliteTestContext();

    public void Dispose() => _ctx.Dispose();

    private RecurringExpenseGenerationService BuildService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:TimeZone"] = "UTC" })
            .Build();
        return new RecurringExpenseGenerationService(
            _ctx.Db, TimeProvider.System, new AppTimeZoneProvider(config), NullLogger<RecurringExpenseGenerationService>.Instance);
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "RE House",
            AddressLine1 = "1 RE Ln",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private RecurringExpense SeedTemplate(int? propertyId, DateTime start, DateTime nextRun,
        RecurringExpenseFrequency freq = RecurringExpenseFrequency.Monthly,
        decimal amount = 250m, ScheduleECategory category = ScheduleECategory.Insurance, bool active = true)
    {
        var now = DateTime.UtcNow;
        var template = new RecurringExpense
        {
            PortfolioId = 1,
            PropertyId = propertyId,
            Category = category,
            Description = "Monthly insurance",
            Amount = amount,
            Frequency = freq,
            StartDate = start,
            NextRunDate = nextRun,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringExpenses.Add(template);
        _ctx.Db.SaveChanges();
        return template;
    }

    [Fact]
    public async Task MaterializesDuePeriod_AndAdvancesSchedule_Idempotently()
    {
        var property = SeedProperty();
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var template = SeedTemplate(property.Id, start: thisMonth, nextRun: thisMonth);

        var sut = BuildService();

        (await sut.GenerateAsync()).Should().Be(1);
        (await sut.GenerateAsync()).Should().Be(0); // schedule advanced → idempotent

        var expenses = _ctx.Db.Expenses.ToList();
        expenses.Should().HaveCount(1);
        var e = expenses[0];
        e.Amount.Should().Be(250m);
        e.Category.Should().Be(ScheduleECategory.Insurance);
        e.PropertyId.Should().Be(property.Id);
        e.IncurredAt.Should().Be(thisMonth);

        // NextRunDate advanced to next month.
        var refreshed = _ctx.Db.RecurringExpenses.Single();
        refreshed.NextRunDate.Should().Be(thisMonth.AddMonths(1));
    }

    [Fact]
    public async Task CatchesUpMissedMonths_OneExpensePerPeriod()
    {
        var property = SeedProperty();
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // NextRunDate 3 months ago → 4 periods due (3 prior + this month).
        var template = SeedTemplate(property.Id, start: thisMonth.AddMonths(-3), nextRun: thisMonth.AddMonths(-3));

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(4);

        _ctx.Db.Expenses.Should().HaveCount(4);
        // Schedule rolled forward past today.
        _ctx.Db.RecurringExpenses.Single().NextRunDate.Should().Be(thisMonth.AddMonths(1));
    }

    [Fact]
    public async Task InactiveTemplate_CreatesNothing()
    {
        var property = SeedProperty();
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedTemplate(property.Id, start: thisMonth, nextRun: thisMonth, active: false);

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(0);
        _ctx.Db.Expenses.Should().BeEmpty();
    }

    [Fact]
    public async Task NotYetDue_CreatesNothing()
    {
        var property = SeedProperty();
        var nextMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        SeedTemplate(property.Id, start: nextMonth, nextRun: nextMonth);

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(0);
        _ctx.Db.Expenses.Should().BeEmpty();
    }

    [Fact]
    public async Task QuarterlyFrequency_AdvancesByThreeMonths()
    {
        var property = SeedProperty();
        var thisMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedTemplate(property.Id, start: thisMonth, nextRun: thisMonth, freq: RecurringExpenseFrequency.Quarterly);

        var sut = BuildService();
        (await sut.GenerateAsync()).Should().Be(1);

        _ctx.Db.RecurringExpenses.Single().NextRunDate.Should().Be(thisMonth.AddMonths(3));
    }
}
