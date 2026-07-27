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
        int earliestExpenseId;

        await using (var seed = NewContext())
        {
            var earliestExpense = Expense(portfolioId, propertyId, now.AddDays(-3), true);
            seed.Loans.AddRange(
                Loan(portfolioId, propertyId, now.AddMonths(-2), LoanStatus.Active),
                Loan(portfolioId, propertyId, now.AddMonths(1), LoanStatus.Active),
                Loan(portfolioId, propertyId, now.AddMonths(-2), LoanStatus.Closed));
            seed.RecurringExpenses.AddRange(
                earliestExpense,
                Expense(portfolioId, propertyId, now.AddDays(-2), true),
                Expense(portfolioId, propertyId, now.AddDays(2), true),
                Expense(portfolioId, propertyId, now.AddDays(-2), false));
            seed.RecurringMaintenanceTasks.AddRange(
                Maintenance(portfolioId, propertyId, now.AddDays(-2), true),
                Maintenance(portfolioId, propertyId, now.AddDays(2), true),
                Maintenance(portfolioId, propertyId, now.AddDays(-2), false));
            await seed.SaveChangesAsync();
            earliestExpenseId = earliestExpense.Id;
        }

        await using var db = NewContext();
        var store = new ScheduledAutomationClaimStore(db);
        (await store.ClaimDebtServiceAsync("debt", now, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
        (await store.ClaimRecurringExpensesAsync("expense", now, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle(claim => claim.Id == earliestExpenseId);
        (await store.ClaimRecurringMaintenanceAsync("maintenance", now, TimeSpan.FromMinutes(2), 1))
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
                "expense-a", now, TimeSpan.FromMinutes(2), 4),
            new ScheduledAutomationClaimStore(dbB).ClaimRecurringExpensesAsync(
                "expense-b", now, TimeSpan.FromMinutes(2), 4));

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
            "expense-replacement", now, TimeSpan.FromMinutes(2), 1)).Single();
        replacement.Id.Should().Be(first.Id);
        replacement.ClaimToken.Should().NotBe(first.ClaimToken);

        await using var fenced = NewContext();
        var durable = await fenced.RecurringExpenses.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == first.Id);
        durable.WorkerClaimToken.Should().Be(replacement.ClaimToken);
        durable.WorkerClaimOwner.Should().Be("expense-replacement");
    }

    [SkippableFact]
    public async Task Maintenance_master_switch_is_enforced_by_claim_query()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var (portfolioId, propertyId) = await SeedScopeAsync(now);

        await using (var seed = NewContext())
        {
            seed.AutomationSettings.Add(new AutomationSettings
            {
                PortfolioId = portfolioId,
                EnableRecurringMaintenance = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            seed.RecurringMaintenanceTasks.Add(Maintenance(portfolioId, propertyId, now.AddDays(-1), true));
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var store = new ScheduledAutomationClaimStore(db);
        (await store.ClaimRecurringMaintenanceAsync(
            "maintenance-disabled", now, TimeSpan.FromMinutes(2), 25)).Should().BeEmpty();

        await db.AutomationSettings
            .Where(row => row.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.EnableRecurringMaintenance, true)
                .SetProperty(row => row.UpdatedAtUtc, now));

        var claims = await store.ClaimRecurringMaintenanceAsync(
            "maintenance-enabled", now, TimeSpan.FromMinutes(2), 25);
        claims.Should().ContainSingle();

        await db.AutomationSettings
            .Where(row => row.PortfolioId == portfolioId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.EnableRecurringMaintenance, false)
                .SetProperty(row => row.UpdatedAtUtc, now));

        (await store.ClaimRecurringMaintenanceAsync(
            "maintenance-disabled-again", now, TimeSpan.FromMinutes(2), 25)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Debt_claim_selection_is_one_statement_and_orders_by_next_due_occurrence()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var (portfolioId, propertyId) = await SeedScopeAsync(now);
        int oldestDebtOccurrenceLoanId;

        await using (var seed = NewContext())
        {
            var loans = Enumerable.Range(0, 3)
                .Select(_ => Loan(portfolioId, propertyId, now.AddMonths(-2), LoanStatus.Active))
                .ToArray();
            seed.Loans.AddRange(loans);
            await seed.SaveChangesAsync();

            seed.LoanPayments.AddRange(loans.Select((loan, index) => new LoanPayment
            {
                PortfolioId = portfolioId,
                LoanId = loan.Id,
                PeriodKey = now.AddMonths(index - 3).ToString("yyyy-MM"),
                DueDate = now.AddMonths(index - 3),
                InterestAmount = 100m,
                PrincipalAmount = 500m,
                TotalAmount = 600m,
                BalanceAfter = 99_500m,
                CreatedAt = now,
            }));
            await seed.SaveChangesAsync();
            oldestDebtOccurrenceLoanId = loans[0].Id;
        }

        await using var db = NewContext();
        var store = new ScheduledAutomationClaimStore(db);
        var claims = await store.ClaimDebtServiceAsync(
            "debt-batch", now, TimeSpan.FromMinutes(2), 1);
        claims.Should().ContainSingle(claim => claim.Id == oldestDebtOccurrenceLoanId);
        ScheduledAutomationClaimStore.DebtClaimStatement
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().ContainSingle();
        ScheduledAutomationClaimStore.DebtClaimStatement.Should()
            .Contain("WITH candidates AS")
            .And.Contain("UPDATE \"Loans\"")
            .And.Contain("RETURNING")
            .And.Contain("LIMIT @batchSize");
    }

    [SkippableFact]
    public async Task Imported_historical_debt_is_not_claimed_until_first_due_date_after_import()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var importDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var firstDueDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var (portfolioId, propertyId) = await SeedScopeAsync(importDate);

        await using (var seed = NewContext())
        {
            seed.Loans.Add(Loan(
                portfolioId,
                propertyId,
                new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
                LoanStatus.Active,
                createdAt: importDate,
                dayOfMonthDue: 12));
            await seed.SaveChangesAsync();
        }

        await using var beforeDue = NewContext();
        (await new ScheduledAutomationClaimStore(beforeDue).ClaimDebtServiceAsync(
                "debt-before-due", importDate, TimeSpan.FromMinutes(2), 1))
            .Should().BeEmpty();

        await using var onDue = NewContext();
        (await new ScheduledAutomationClaimStore(onDue).ClaimDebtServiceAsync(
                "debt-on-due", firstDueDate, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Imported_historical_debt_created_after_due_day_is_not_claimed_until_next_month_due_date()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var importDate = new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc);
        var nextDueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc);
        var (portfolioId, propertyId) = await SeedScopeAsync(importDate);

        await using (var seed = NewContext())
        {
            seed.Loans.Add(Loan(
                portfolioId,
                propertyId,
                new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
                LoanStatus.Active,
                createdAt: importDate,
                dayOfMonthDue: 12));
            await seed.SaveChangesAsync();
        }

        await using var beforeNextDue = NewContext();
        (await new ScheduledAutomationClaimStore(beforeNextDue).ClaimDebtServiceAsync(
                "debt-before-next-due", importDate, TimeSpan.FromMinutes(2), 1))
            .Should().BeEmpty();

        await using var onNextDue = NewContext();
        (await new ScheduledAutomationClaimStore(onNextDue).ClaimDebtServiceAsync(
                "debt-on-next-due", nextDueDate, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Imported_historical_debt_with_pre_import_tail_is_not_claimed_before_import_due_date()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var importDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var firstDueDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var (portfolioId, propertyId) = await SeedScopeAsync(importDate);

        await using (var seed = NewContext())
        {
            var loan = Loan(
                portfolioId,
                propertyId,
                new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
                LoanStatus.Active,
                createdAt: importDate,
                dayOfMonthDue: 12);
            seed.Loans.Add(loan);
            await seed.SaveChangesAsync();
            seed.LoanPayments.Add(new LoanPayment
            {
                PortfolioId = portfolioId,
                LoanId = loan.Id,
                PeriodKey = "2026-07",
                DueDate = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 100m,
                PrincipalAmount = 500m,
                TotalAmount = 600m,
                BalanceAfter = 50_000m,
                CreatedAt = importDate,
            });
            await seed.SaveChangesAsync();
        }

        await using var beforeDue = NewContext();
        var store = new ScheduledAutomationClaimStore(beforeDue);
        (await store.ClaimDebtServiceAsync(
                "debt-before-due-first", importDate, TimeSpan.FromMinutes(2), 1))
            .Should().BeEmpty();
        (await store.ClaimDebtServiceAsync(
                "debt-before-due-second", importDate, TimeSpan.FromMinutes(2), 1))
            .Should().BeEmpty();

        await using var onDue = NewContext();
        (await new ScheduledAutomationClaimStore(onDue).ClaimDebtServiceAsync(
                "debt-on-due", firstDueDate, TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
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

    private static Loan Loan(
        int portfolioId,
        int propertyId,
        DateTime start,
        LoanStatus status,
        DateTime? createdAt = null,
        int dayOfMonthDue = 1) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Lender = "Claim Bank",
        OriginalAmount = 100_000m,
        CurrentBalance = 100_000m,
        AnnualInterestRatePct = 5m,
        TermMonths = 360,
        StartDate = start,
        DayOfMonthDue = dayOfMonthDue,
        MonthlyPrincipalInterest = 600m,
        Status = status,
        CreatedAt = createdAt ?? start,
        UpdatedAt = createdAt ?? start,
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
