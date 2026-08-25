using FluentAssertions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Portfolio-scoped recurring-expense reads, SQL-side paging, and the contract that all business
/// mutations flow through scoped receipt-backed commands.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name2)]
public class RecurringExpenseServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RecurringExpenseService _sut = null!;
    private WorkspaceReadScope _scope;

    public RecurringExpenseServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(RecurringExpenseServiceTests));
        _sut = new RecurringExpenseService(
            _ctx.Db, TimeProvider.System, Mock.Of<IWriteExecutor>());
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public void MutationsExposeOnlyScopedReceiptBackedOverloads()
    {
        var mutationMethods = typeof(IRecurringExpenseService).GetMethods()
            .Where(method => method.Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync")
            .ToArray();

        mutationMethods.Should().HaveCount(3);
        mutationMethods.Should().OnlyContain(method =>
            method.GetParameters().First().ParameterType == typeof(RentalCommand.Core.Authorization.WorkspaceReadScope) &&
            method.GetParameters().Any(parameter => parameter.Name == "idempotencyKey"));
    }

    [Fact]
    public async Task ListPageAsync_FiltersByNextRunDate_AndReturnsPagedMetadata()
    {
        var property = SeedProperty();
        SeedTemplate(property.Id, description: "January insurance", nextRunDate: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        SeedTemplate(property.Id, description: "February insurance", nextRunDate: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedTemplate(property.Id, description: "March insurance", nextRunDate: new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));

        await _ctx.ActivateApiScopeAsync(_scope);
        var page = await _sut.ListPageAsync(_scope, property.Id, new ListQuery
        {
            From = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Sort = "-nextRunDate",
            Skip = 0,
            Take = 1,
        });

        page.TotalCount.Should().Be(2);
        page.Skip.Should().Be(0);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].Description.Should().Be("March insurance");
        page.Items[0].PropertyName.Should().Be(property.Name);
    }

    // -----------------------------------------------------------------------
    // Helpers

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

    private RecurringExpense SeedTemplate(
        int propertyId,
        string description = "Monthly insurance",
        DateTime? nextRunDate = null)
    {
        var now = DateTime.UtcNow;
        var nextRun = nextRunDate ?? now.Date;
        var template = new RecurringExpense
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            Category = ScheduleECategory.Insurance,
            Description = description,
            Amount = 250m,
            Frequency = RecurringExpenseFrequency.Monthly,
            StartDate = nextRun,
            NextRunDate = nextRun,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringExpenses.Add(template);
        _ctx.Db.SaveChanges();
        return template;
    }
}
