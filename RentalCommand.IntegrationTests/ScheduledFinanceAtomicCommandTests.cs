using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Automation;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class ScheduledFinanceAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> ExpenseCodec =
        new("scheduled-finance.recurring-expense.apply.v1");
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> DebtCodec =
        new("scheduled-finance.debt-service.apply.v1");
    private readonly DateTime _today = new(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _propertyId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_scheduled_finance_atomic")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandRecorder>();
        services.AddSingleton<CompanionFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            ApplyClaimedDebtServiceBatchCommand,
            ApplyScheduledFinanceBatchResult,
            ApplyClaimedDebtServiceBatchHandler>();
        services.AddAtomicCommandHandler<
            ApplyClaimedRecurringExpenseBatchCommand,
            ApplyScheduledFinanceBatchResult,
            ApplyClaimedRecurringExpenseBatchHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandRecorder>(),
                    provider.GetRequiredService<CompanionFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var property = new Property
        {
            Portfolio = new Portfolio
            {
                Name = "Scheduled finance atomic",
                ManagementCompanyName = "Scheduled finance atomic",
                TimeZone = "UTC",
                CreatedAt = _today,
                UpdatedAt = _today,
            },
            Name = "Finance House",
            AddressLine1 = "1 Atomic Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = _today,
            UpdatedAt = _today,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        _portfolioId = property.PortfolioId;
        _propertyId = property.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task RecurringExpense_ReplayReturnsCanonicalResult_AndUsesOneScheduleRead()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today.AddMonths(-1));
        var claim = await ClaimExpenseAsync();
        Recorder.Clear();
        var identity = ExpenseIdentity(claim.ClaimToken, "canonical");
        var command = ExpenseCommand(claim);

        var first = await Atomic.ExecuteAsync(identity, command, ExpenseCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, ExpenseCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        first.Value.GeneratedRowCount.Should().Be(2);
        Recorder.Commands.Count(sql =>
                sql.Contains("FROM \"RecurringExpenses\" AS template", StringComparison.Ordinal)
                && sql.Contains("FOR UPDATE OF template", StringComparison.Ordinal))
            .Should().Be(1);

        await using var verify = NewContext();
        (await verify.Expenses.CountAsync(row => row.RecurringExpenseId == templateId)).Should().Be(2);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        var template = await verify.RecurringExpenses.SingleAsync(row => row.Id == templateId);
        template.NextRunDate.Should().Be(_today.AddMonths(1));
        template.WorkerClaimToken.Should().BeNull();
    }

    [SkippableFact]
    public async Task DebtService_ReplayCreatesOneImmutableOccurrence_AndCanonicalReceipt()
    {
        SkipIfNoDocker();
        var loanId = await SeedLoanAsync();
        var claim = await ClaimDebtAsync();
        var identity = DebtIdentity(claim.ClaimToken, "canonical");
        var command = new ApplyClaimedDebtServiceBatchCommand(
            [claim.Id], claim.ClaimToken, _today, _today.AddMinutes(1));

        var first = await Atomic.ExecuteAsync(identity, command, DebtCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, DebtCodec);

        first.Value.GeneratedRowCount.Should().Be(1);
        replay.Value.Should().Be(first.Value);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = NewContext();
        (await verify.LoanPayments.CountAsync(row => row.LoanId == loanId)).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType)).Should().BeGreaterThanOrEqualTo(2);
    }

    [SkippableFact]
    public async Task RecurringExpense_BoundedCatchUpLeavesEveryOlderOccurrenceRecoverable()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today.AddMonths(-40));
        var firstClaim = await ClaimExpenseAsync();

        var first = await Atomic.ExecuteAsync(
            ExpenseIdentity(firstClaim.ClaimToken, "bounded-first"),
            ExpenseCommand(firstClaim),
            ExpenseCodec);

        first.Value.GeneratedRowCount.Should().Be(36);
        var secondClaim = await ClaimExpenseAsync();
        var second = await Atomic.ExecuteAsync(
            ExpenseIdentity(secondClaim.ClaimToken, "bounded-second"),
            ExpenseCommand(secondClaim),
            ExpenseCodec);

        second.Value.GeneratedRowCount.Should().Be(5);
        await using var verify = NewContext();
        (await verify.Expenses.CountAsync(row => row.RecurringExpenseId == templateId)).Should().Be(41);
        (await verify.RecurringExpenses.SingleAsync(row => row.Id == templateId)).NextRunDate
            .Should().Be(_today.AddMonths(1));
    }

    [SkippableFact]
    public async Task DebtService_BoundedCatchUpContinuesWithoutSkippingPeriods()
    {
        SkipIfNoDocker();
        var loanId = await SeedLoanAsync(_today.AddMonths(-40));
        var firstClaim = await ClaimDebtAsync();
        var first = await Atomic.ExecuteAsync(
            DebtIdentity(firstClaim.ClaimToken, "bounded-first"),
            new ApplyClaimedDebtServiceBatchCommand(
                [firstClaim.Id], firstClaim.ClaimToken, _today, _today.AddMinutes(1)),
            DebtCodec);

        first.Value.GeneratedRowCount.Should().Be(36);
        var secondClaim = await ClaimDebtAsync();
        var second = await Atomic.ExecuteAsync(
            DebtIdentity(secondClaim.ClaimToken, "bounded-second"),
            new ApplyClaimedDebtServiceBatchCommand(
                [secondClaim.Id], secondClaim.ClaimToken, _today, _today.AddMinutes(2)),
            DebtCodec);

        second.Value.GeneratedRowCount.Should().Be(5);
        await using var verify = NewContext();
        var periods = await verify.LoanPayments
            .Where(row => row.LoanId == loanId)
            .OrderBy(row => row.PeriodKey)
            .Select(row => row.PeriodKey)
            .ToListAsync();
        periods.Should().HaveCount(41).And.OnlyHaveUniqueItems();
        periods.Should().ContainInOrder(
            Enumerable.Range(0, 41)
                .Select(offset => _today.AddMonths(-40 + offset).ToString("yyyy-MM")));
    }

    [SkippableFact]
    public async Task ExpiredTakenOverToken_WritesNothing_AndReplacementRecovers()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today);
        var stale = await ClaimExpenseAsync();
        await using (var expire = NewContext())
        {
            await expire.RecurringExpenses.Where(row => row.Id == templateId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.WorkerClaimExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        var replacement = await ClaimExpenseAsync();
        var staleIdentity = ExpenseIdentity(stale.ClaimToken, "stale");

        await FluentActions.Invoking(() => Atomic.ExecuteAsync(
                staleIdentity, ExpenseCommand(stale), ExpenseCodec))
            .Should().ThrowAsync<ScheduledFinanceClaimLostException>();

        await using (var verifyStale = NewContext())
        {
            (await verifyStale.Expenses.CountAsync(row => row.RecurringExpenseId == templateId)).Should().Be(0);
            (await verifyStale.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == staleIdentity.CommandType
                && row.IdempotencyKey == staleIdentity.IdempotencyKey)).Should().Be(0);
            (await verifyStale.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == staleIdentity.CommandType)).Should().Be(0);
            var durable = await verifyStale.RecurringExpenses.SingleAsync(row => row.Id == templateId);
            durable.WorkerClaimToken.Should().Be(replacement.ClaimToken);
            durable.NextRunDate.Should().Be(_today);
        }

        (await Atomic.ExecuteAsync(
            ExpenseIdentity(replacement.ClaimToken, "replacement"),
            ExpenseCommand(replacement),
            ExpenseCodec)).Value.GeneratedRowCount.Should().Be(1);
    }

    [SkippableTheory]
    [InlineData("audit")]
    [InlineData("receipt")]
    public async Task InjectedCompanionFailure_RollsBackBusinessAuditReceiptClaimAndSchedule_ThenRecovers(
        string failure)
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today);
        var claim = await ClaimExpenseAsync();
        var identity = ExpenseIdentity(claim.ClaimToken, failure);
        Failures.FailAtomicAudit = failure == "audit";
        Failures.FailReceiptCompletion = failure == "receipt";

        await FluentActions.Invoking(() => Atomic.ExecuteAsync(
                identity, ExpenseCommand(claim), ExpenseCodec))
            .Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<InvalidOperationException>();
        Failures.FailAtomicAudit = false;
        Failures.FailReceiptCompletion = false;

        await using (var failed = NewContext())
        {
            (await failed.Expenses.CountAsync(row => row.RecurringExpenseId == templateId)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(row => row.CommandType == identity.CommandType)).Should().Be(0);
            (await failed.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            var template = await failed.RecurringExpenses.SingleAsync(row => row.Id == templateId);
            template.NextRunDate.Should().Be(_today);
            template.WorkerClaimToken.Should().Be(claim.ClaimToken);
        }

        (await Atomic.ExecuteAsync(identity, ExpenseCommand(claim), ExpenseCodec))
            .Value.GeneratedRowCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task DifferentOperationKey_CannotDuplicateRecurringScheduleOccurrence()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today);
        var firstClaim = await ClaimExpenseAsync();
        await Atomic.ExecuteAsync(
            ExpenseIdentity(firstClaim.ClaimToken, "first"), ExpenseCommand(firstClaim), ExpenseCodec);

        var secondToken = Guid.NewGuid();
        await using (var rewind = NewContext())
        {
            await rewind.RecurringExpenses.Where(row => row.Id == templateId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.NextRunDate, _today)
                    .SetProperty(row => row.WorkerClaimOwner, "duplicate-test")
                    .SetProperty(row => row.WorkerClaimToken, secondToken)
                    .SetProperty(row => row.WorkerClaimExpiresAtUtc, DateTime.UtcNow.AddMinutes(5)));
        }
        var secondIdentity = ExpenseIdentity(secondToken, "different-key");

        await FluentActions.Invoking(() => Atomic.ExecuteAsync(
                secondIdentity,
                new ApplyClaimedRecurringExpenseBatchCommand(
                    [templateId], secondToken, _today, _today.AddMinutes(1)),
                ExpenseCodec))
            .Should().ThrowAsync<DbUpdateException>();

        await using var verify = NewContext();
        (await verify.Expenses.CountAsync(row => row.RecurringExpenseId == templateId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == secondIdentity.CommandType
            && row.IdempotencyKey == secondIdentity.IdempotencyKey)).Should().Be(0);
        var template = await verify.RecurringExpenses.SingleAsync(row => row.Id == templateId);
        template.NextRunDate.Should().Be(_today);
        template.WorkerClaimToken.Should().Be(secondToken);
    }

    private async Task<int> SeedRecurringExpenseAsync(DateTime nextRunDate)
    {
        await using var db = NewContext();
        var template = new RecurringExpense
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            Category = ScheduleECategory.Insurance,
            Description = "Insurance",
            Amount = 250m,
            Frequency = RecurringExpenseFrequency.Monthly,
            StartDate = nextRunDate,
            NextRunDate = nextRunDate,
            Active = true,
            CreatedAt = _today,
            UpdatedAt = _today,
        };
        db.RecurringExpenses.Add(template);
        await db.SaveChangesAsync();
        return template.Id;
    }

    private async Task<int> SeedLoanAsync(DateTime? startDate = null)
    {
        await using var db = NewContext();
        var loan = new Loan
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            Lender = "Atomic Bank",
            OriginalAmount = 100_000m,
            CurrentBalance = 100_000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = startDate ?? _today,
            DayOfMonthDue = 1,
            MonthlyPrincipalInterest = 600m,
            Status = LoanStatus.Active,
            CreatedAt = _today,
            UpdatedAt = _today,
        };
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan.Id;
    }

    private async Task<ScheduledAutomationClaim> ClaimExpenseAsync()
    {
        await using var db = NewContext();
        return (await new ScheduledAutomationClaimStore(db).ClaimRecurringExpensesAsync(
            "expense-test", _today, TimeSpan.FromMinutes(5), 1)).Single();
    }

    private async Task<ScheduledAutomationClaim> ClaimDebtAsync()
    {
        await using var db = NewContext();
        return (await new ScheduledAutomationClaimStore(db).ClaimDebtServiceAsync(
            "debt-test", _today, TimeSpan.FromMinutes(5), 1)).Single();
    }

    private ApplyClaimedRecurringExpenseBatchCommand ExpenseCommand(ScheduledAutomationClaim claim) =>
        new([claim.Id], claim.ClaimToken, _today, _today.AddMinutes(1));

    private static AtomicCommandIdentity ExpenseIdentity(Guid token, string suffix) =>
        new("scheduled-finance.recurring-expense.apply", $"{token:N}:{suffix}");

    private static AtomicCommandIdentity DebtIdentity(Guid token, string suffix) =>
        new("scheduled-finance.debt-service.apply", $"{token:N}:{suffix}");

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
    private CommandRecorder Recorder => _services!.GetRequiredService<CommandRecorder>();
    private CompanionFailureInterceptor Failures =>
        _services!.GetRequiredService<CompanionFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for scheduled-finance atomic tests.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:scheduled-finance";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CompanionFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }
        public bool FailReceiptCompletion { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private void MaybeFail(string sql)
        {
            if (FailAtomicAudit && sql.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
                throw new InvalidOperationException("injected atomic audit failure");
            if (FailReceiptCompletion
                && sql.Contains("UPDATE \"AtomicCommandReceipts\"", StringComparison.Ordinal))
                throw new InvalidOperationException("injected receipt completion failure");
        }
    }
}
