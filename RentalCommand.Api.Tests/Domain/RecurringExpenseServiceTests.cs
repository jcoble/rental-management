using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Portfolio-scoped CRUD for recurring-expense templates: create validates property/unit
/// in-portfolio (the IDOR guard), defaults NextRunDate to StartDate, update/soft-delete behave, and
/// the soft-delete query filter hides removed templates.
/// </summary>
public class RecurringExpenseServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly RecurringExpenseService _sut;

    public RecurringExpenseServiceTests()
    {
        _sut = new RecurringExpenseService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task CreateAsync_PersistsTemplate_DefaultsNextRunToStart()
    {
        var property = SeedProperty();

        var result = await _sut.CreateAsync(PortfolioId, new CreateRecurringExpenseRequest
        {
            PropertyId = property.Id,
            Category = ScheduleECategory.Insurance,
            Description = "Annual policy",
            Amount = 1_200m,
            Frequency = RecurringExpenseFrequency.Annual,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        result.Should().NotBeNull();
        result!.Description.Should().Be("Annual policy");
        result.Frequency.Should().Be(RecurringExpenseFrequency.Annual);
        result.NextRunDate.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        result.PropertyName.Should().Be(property.Name);
        result.Active.Should().BeTrue();
        _ctx.Db.RecurringExpenses.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateAsync_RejectsPropertyOutsidePortfolio()
    {
        SeedPortfolio(999);
        var foreign = SeedProperty(portfolioId: 999);

        var result = await _sut.CreateAsync(PortfolioId, new CreateRecurringExpenseRequest
        {
            PropertyId = foreign.Id,
            Description = "Should not link",
            Amount = 100m,
            StartDate = DateTime.UtcNow,
        });

        result.Should().BeNull();
        _ctx.Db.RecurringExpenses.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ChangesFields()
    {
        var property = SeedProperty();
        var template = SeedTemplate(property.Id);

        var result = await _sut.UpdateAsync(PortfolioId, template.Id, new UpdateRecurringExpenseRequest
        {
            Amount = 999m,
            Frequency = RecurringExpenseFrequency.Quarterly,
            Active = false,
        });

        result.Should().NotBeNull();
        result!.Amount.Should().Be(999m);
        result.Frequency.Should().Be(RecurringExpenseFrequency.Quarterly);
        result.Active.Should().BeFalse();
    }

    [Fact]
    public async Task CrossPortfolio_CannotReadOrMutate()
    {
        var property = SeedProperty();
        var template = SeedTemplate(property.Id);

        (await _sut.GetAsync(portfolioId: 2, template.Id)).Should().BeNull();
        (await _sut.UpdateAsync(portfolioId: 2, template.Id, new UpdateRecurringExpenseRequest { Amount = 1m })).Should().BeNull();
        (await _sut.DeleteAsync(portfolioId: 2, template.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_AndHidesFromReads()
    {
        var property = SeedProperty();
        var template = SeedTemplate(property.Id);

        (await _sut.DeleteAsync(PortfolioId, template.Id)).Should().BeTrue();

        (await _sut.GetAsync(PortfolioId, template.Id)).Should().BeNull();
        (await _sut.ListAsync(PortfolioId, propertyId: null, new ListQuery())).Should().BeEmpty();
        _ctx.Db.RecurringExpenses.IgnoreQueryFilters().Should().HaveCount(1);
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

    private RecurringExpense SeedTemplate(int propertyId)
    {
        var now = DateTime.UtcNow;
        var template = new RecurringExpense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Category = ScheduleECategory.Insurance,
            Description = "Monthly insurance",
            Amount = 250m,
            Frequency = RecurringExpenseFrequency.Monthly,
            StartDate = now.Date,
            NextRunDate = now.Date,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringExpenses.Add(template);
        _ctx.Db.SaveChanges();
        return template;
    }
}
