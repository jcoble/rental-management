using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Automation;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for bounded scheduled-automation leases.</summary>
public sealed class ScheduledAutomationClaimStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _dockerAvailable = true;
        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Due_queues_claim_only_eligible_rows_in_bounded_order()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var (portfolioId, propertyId) = await SeedScopeAsync(now);

        await using (var seed = NewContext())
        {
            seed.Loans.AddRange(
                Loan(portfolioId, propertyId, now.AddMonths(-2), LoanStatus.Active),
                Loan(portfolioId, propertyId, now.AddMonths(1), LoanStatus.Active),
                Loan(portfolioId, propertyId, now.AddMonths(-2), LoanStatus.Closed));
            seed.RecurringExpenses.AddRange(
                Expense(portfolioId, propertyId, now.AddDays(-2), true),
                Expense(portfolioId, propertyId, now.AddDays(2), true),
                Expense(portfolioId, propertyId, now.AddDays(-2), false));
            seed.RecurringMaintenanceTasks.AddRange(
                Maintenance(portfolioId, propertyId, now.AddDays(-2), true),
                Maintenance(portfolioId, propertyId, now.AddDays(2), true),
                Maintenance(portfolioId, propertyId, now.AddDays(-2), false));
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var store = new ScheduledAutomationClaimStore(db);
        (await store.ClaimDebtServiceAsync("debt", now, now, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
        (await store.ClaimRecurringExpensesAsync("expense", now, now, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
        (await store.ClaimRecurringMaintenanceAsync("maintenance", now, now, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Concurrent_claims_are_disjoint_and_expired_lease_is_reclaimed()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var (portfolioId, propertyId) = await SeedScopeAsync(now);
        await using (var seed = NewContext())
        {
            seed.RecurringExpenses.AddRange(Enumerable.Range(1, 8)
                .Select(index => Expense(portfolioId, propertyId, now.AddDays(-index), true)));
            await seed.SaveChangesAsync();
        }

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var batches = await Task.WhenAll(
            new ScheduledAutomationClaimStore(dbA).ClaimRecurringExpensesAsync(
                "expense-a", now, now, TimeSpan.FromMinutes(2), 4),
            new ScheduledAutomationClaimStore(dbB).ClaimRecurringExpensesAsync(
                "expense-b", now, now, TimeSpan.FromMinutes(2), 4));

        var all = batches.SelectMany(batch => batch).ToArray();
        all.Should().HaveCount(8);
        all.Select(claim => claim.Id).Should().OnlyHaveUniqueItems();

        var first = all[0];
        await using (var expire = NewContext())
        {
            await expire.RecurringExpenses.Where(row => row.Id == first.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.WorkerClaimExpiresAtUtc, now.AddSeconds(-1)));
        }

        await using var reclaim = NewContext();
        var replacement = (await new ScheduledAutomationClaimStore(reclaim).ClaimRecurringExpensesAsync(
            "expense-replacement", now, now, TimeSpan.FromMinutes(2), 1)).Single();
        replacement.Id.Should().Be(first.Id);
        replacement.ClaimToken.Should().NotBe(first.ClaimToken);
    }

    private async Task<(int PortfolioId, int PropertyId)> SeedScopeAsync(DateTime now)
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Name = "Scheduled claims",
            ManagementCompanyName = "Scheduled claims",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = "Claim property",
            AddressLine1 = "1 Claim Way",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (portfolio.Id, property.Id);
    }

    private static Loan Loan(int portfolioId, int propertyId, DateTime start, LoanStatus status) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Lender = "Claim Bank",
        OriginalAmount = 100_000m,
        CurrentBalance = 100_000m,
        AnnualInterestRatePct = 5m,
        TermMonths = 360,
        StartDate = start,
        MonthlyPrincipalInterest = 600m,
        Status = status,
        CreatedAt = start,
        UpdatedAt = start,
    };

    private static RecurringExpense Expense(int portfolioId, int propertyId, DateTime due, bool active) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Description = "Insurance",
        Amount = 100m,
        StartDate = due,
        NextRunDate = due,
        Active = active,
        CreatedAt = due,
        UpdatedAt = due,
    };

    private static RecurringMaintenanceTask Maintenance(
        int portfolioId, int propertyId, DateTime due, bool active) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Title = "Filter",
        NextDueDate = due,
        IsActive = active,
        CreatedAt = due,
        UpdatedAt = due,
    };

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);
}
