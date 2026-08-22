using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Automation;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

public sealed class ScheduledFinanceAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> ExpenseCodec =
        new("scheduled-finance.recurring-expense.apply.v1");
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> DebtCodec =
        new("scheduled-finance.debt-service.apply.v1");
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> MaintenanceCodec =
        new("scheduled-automation.recurring-maintenance.apply.v1");
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
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
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
        await new ChartOfAccountsSeedService(db).SeedAsync(_portfolioId);
        await db.SaveChangesAsync();
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

        var first = await ExecuteAtomicAsync(identity, command, ExpenseCodec);
        var replay = await ExecuteAtomicAsync(identity, command, ExpenseCodec);

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
        var claim = (await ClaimDebtAsync()).Single();
        var identity = DebtIdentity(claim.ClaimToken, "canonical");
        var command = new ApplyClaimedDebtServiceBatchCommand(
            [claim.Id], claim.ClaimToken, _today, _today.AddMinutes(1));

        Recorder.Clear();
        var first = await ExecuteAtomicAsync(identity, command, DebtCodec);
        var replay = await ExecuteAtomicAsync(identity, command, DebtCodec);

        first.Value.GeneratedRowCount.Should().Be(1);
        replay.Value.Should().Be(first.Value);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        var effectiveTailCommands = Recorder.Commands.Where(sql =>
            sql.Contains("FROM \"LoanPayments\"", StringComparison.Ordinal) &&
            sql.Contains("\"LoanPaymentCorrections\"", StringComparison.Ordinal)).ToArray();
        effectiveTailCommands.Should().ContainSingle(
            "the Engine must select and shape all effective loan tails in one reader command");
        effectiveTailCommands[0].Should()
            .Contain("ORDER BY")
            .And.Contain("GROUP BY")
            .And.Contain("ROW_NUMBER()")
            .And.Contain("row <= 1");
        await using var verify = NewContext();
        (await verify.LoanPayments.CountAsync(row => row.LoanId == loanId)).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType)).Should().BeGreaterThanOrEqualTo(2);
    }

    [SkippableFact]
    public async Task DebtService_FrozenLegacyReceiptReplaysWithoutCreatingPayment()
    {
        SkipIfNoDocker();
        var loanId = await SeedLoanAsync();
        var token = Guid.Parse("72f38a54-6d9a-46dc-aef8-2c3805f85419");
        var identity = new AtomicCommandIdentity(
            "scheduled-finance.debt-service.apply", token.ToString("N"));
        var command = new ApplyClaimedDebtServiceBatchCommand(
            [loanId], token, _today, _today.AddMinutes(1));
        var stored = new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied, 1, 7);
        await SeedLegacyReceiptAsync(identity, command, DebtCodec, stored);

        var replay = await ExecuteAtomicAsync(identity, command, DebtCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(stored);
        await using var verify = NewContext();
        (await verify.LoanPayments.CountAsync(row => row.LoanId == loanId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task RecurringExpense_FrozenLegacyReceiptReplaysWithoutCreatingExpense()
    {
        SkipIfNoDocker();
        var scheduleId = await SeedRecurringExpenseAsync(_today);
        var token = Guid.Parse("6ca25372-606c-42b9-b0e0-ee6e7cbd7238");
        var identity = new AtomicCommandIdentity(
            "scheduled-finance.recurring-expense.apply", token.ToString("N"));
        var command = new ApplyClaimedRecurringExpenseBatchCommand(
            [scheduleId], token, _today, _today.AddMinutes(1));
        var stored = new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied, 1, 8);
        await SeedLegacyReceiptAsync(identity, command, ExpenseCodec, stored);

        var replay = await ExecuteAtomicAsync(identity, command, ExpenseCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(stored);
        await using var verify = NewContext();
        (await verify.Expenses.CountAsync(row => row.RecurringExpenseId == scheduleId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task RecurringMaintenance_FrozenLegacyReceiptReplaysWithoutCreatingWorkOrder()
    {
        SkipIfNoDocker();
        var scheduleId = await SeedRecurringMaintenanceAsync(_today);
        var token = Guid.Parse("1d28f944-7fe9-4aef-95c4-1983569a1609");
        var identity = new AtomicCommandIdentity(
            "scheduled-automation.recurring-maintenance.apply", $"{token:N}:{scheduleId}");
        var command = new ApplyClaimedRecurringMaintenanceBatchCommand(
            [scheduleId], token, _today, _today.AddMinutes(1), "America/New_York");
        var stored = new ApplyScheduledFinanceBatchResult(
            ScheduledFinanceApplyOutcome.Applied, 1, 9);
        await SeedLegacyReceiptAsync(identity, command, MaintenanceCodec, stored);

        var replay = await ExecuteAtomicAsync(identity, command, MaintenanceCodec);

        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(stored);
        await using var verify = NewContext();
        (await verify.WorkOrders.CountAsync(row =>
            row.RecurringMaintenanceTaskId == scheduleId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task DebtService_February20RerunPreservesCorrectedPaymentsBalancesAuditsAndOutbox()
    {
        SkipIfNoDocker();
        var february20 = new DateTime(2027, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var statementAttemptId = Guid.NewGuid();
        int arborLoanId;
        int arborPaymentId;
        int briarLoanId;
        int briarPaymentId;
        await using (var seed = NewContext())
        {
            var arbor = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = _propertyId,
                Lender = "Arbor correction-aware rerun",
                OriginalAmount = 200_000m,
                CurrentBalance = 124_963m,
                AnnualInterestRatePct = 4.5m,
                TermMonths = 360,
                StartDate = new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
                DebtServiceAutomationStartDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                DayOfMonthDue = 20,
                MonthlyPrincipalInterest = 1_046m,
                MonthlyEscrow = 318m,
                Status = LoanStatus.Active,
                CreatedAt = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = february20,
            };
            var briar = new Loan
            {
                PortfolioId = _portfolioId,
                PropertyId = _propertyId,
                Lender = "Briar correction-aware rerun",
                OriginalAmount = 200_000m,
                CurrentBalance = 130_016m,
                AnnualInterestRatePct = 5.8m,
                TermMonths = 360,
                StartDate = new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
                DebtServiceAutomationStartDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                DayOfMonthDue = 20,
                MonthlyPrincipalInterest = 1_070m,
                MonthlyEscrow = 318m,
                Status = LoanStatus.Active,
                CreatedAt = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = february20,
            };
            seed.Loans.AddRange(arbor, briar);
            await seed.SaveChangesAsync();

            var arborPayment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = arbor.Id,
                PeriodKey = "2027-02",
                DueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 470.23m,
                PrincipalAmount = 583.77m,
                EscrowAmount = 318m,
                TotalAmount = 1_372m,
                BalanceAfter = 124_810.23m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
            };
            var briarPayment = new LoanPayment
            {
                PortfolioId = _portfolioId,
                LoanId = briar.Id,
                PeriodKey = "2027-02",
                DueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
                InterestAmount = 633.81m,
                PrincipalAmount = 444.19m,
                EscrowAmount = 0m,
                TotalAmount = 1_078m,
                BalanceAfter = 130_013.81m,
                Status = LoanPaymentStatus.Scheduled,
                CreatedAt = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.LoanPayments.AddRange(arborPayment, briarPayment);
            await seed.SaveChangesAsync();

            var arborCorrection = new LoanPaymentCorrection
            {
                PortfolioId = _portfolioId,
                LoanPaymentId = arborPayment.Id,
                AttemptId = statementAttemptId,
                DueDate = february20,
                PaidDate = february20,
                InterestAmount = 615m,
                PrincipalAmount = 431m,
                EscrowAmount = 318m,
                TotalAmount = 1_364m,
                BalanceAfter = 124_963m,
                Status = LoanPaymentStatus.Paid,
                CreatedAtUtc = february20,
            };
            var briarCorrection = new LoanPaymentCorrection
            {
                PortfolioId = _portfolioId,
                LoanPaymentId = briarPayment.Id,
                AttemptId = statementAttemptId,
                DueDate = february20,
                PaidDate = february20,
                InterestAmount = 628m,
                PrincipalAmount = 442m,
                EscrowAmount = 318m,
                TotalAmount = 1_388m,
                BalanceAfter = 130_016m,
                Status = LoanPaymentStatus.Paid,
                CreatedAtUtc = february20,
            };
            seed.LoanPaymentCorrections.AddRange(arborCorrection, briarCorrection);
            await seed.SaveChangesAsync();

            seed.AtomicAuditLogs.AddRange(
                new AtomicAuditLog
                {
                    AttemptId = statementAttemptId,
                    CommandType = "scan-confirmation.confirm",
                    CommandIdempotencyKey = "ys295:arbor",
                    MutationOrdinal = 1,
                    PortfolioId = _portfolioId,
                    ActorLabel = "integration:scheduled-finance",
                    EntityType = nameof(LoanPaymentCorrection),
                    EntityId = arborCorrection.Id,
                    Operation = AuditLogOperation.Created,
                    Timestamp = february20,
                },
                new AtomicAuditLog
                {
                    AttemptId = statementAttemptId,
                    CommandType = "scan-confirmation.confirm",
                    CommandIdempotencyKey = "ys295:briar",
                    MutationOrdinal = 1,
                    PortfolioId = _portfolioId,
                    ActorLabel = "integration:scheduled-finance",
                    EntityType = nameof(LoanPaymentCorrection),
                    EntityId = briarCorrection.Id,
                    Operation = AuditLogOperation.Created,
                    Timestamp = february20,
                },
                new AtomicAuditLog
                {
                    AttemptId = statementAttemptId,
                    CommandType = "scan-confirmation.confirm",
                    CommandIdempotencyKey = "ys295:arbor",
                    MutationOrdinal = 2,
                    PortfolioId = _portfolioId,
                    ActorLabel = "integration:scheduled-finance",
                    EntityType = nameof(Loan),
                    EntityId = arbor.Id,
                    Operation = AuditLogOperation.Updated,
                    Timestamp = february20,
                },
                new AtomicAuditLog
                {
                    AttemptId = statementAttemptId,
                    CommandType = "scan-confirmation.confirm",
                    CommandIdempotencyKey = "ys295:briar",
                    MutationOrdinal = 2,
                    PortfolioId = _portfolioId,
                    ActorLabel = "integration:scheduled-finance",
                    EntityType = nameof(Loan),
                    EntityId = briar.Id,
                    Operation = AuditLogOperation.Updated,
                    Timestamp = february20,
                });
            seed.OutboxMessages.AddRange(
                new OutboxMessage
                {
                    PortfolioId = _portfolioId,
                    MessageType = "data-update",
                    Payload = "{}",
                    IdempotencyKey = $"ys295:LoanPayment:{arborPayment.Id}:data-update",
                    CreatedAtUtc = february20,
                    NextAttemptAtUtc = february20,
                },
                new OutboxMessage
                {
                    PortfolioId = _portfolioId,
                    MessageType = "data-update",
                    Payload = "{}",
                    IdempotencyKey = $"ys295:LoanPayment:{briarPayment.Id}:data-update",
                    CreatedAtUtc = february20,
                    NextAttemptAtUtc = february20,
                },
                new OutboxMessage
                {
                    PortfolioId = _portfolioId,
                    MessageType = "data-update",
                    Payload = "{}",
                    IdempotencyKey = $"ys295:Loan:{arbor.Id}:data-update",
                    CreatedAtUtc = february20,
                    NextAttemptAtUtc = february20,
                },
                new OutboxMessage
                {
                    PortfolioId = _portfolioId,
                    MessageType = "data-update",
                    Payload = "{}",
                    IdempotencyKey = $"ys295:Loan:{briar.Id}:data-update",
                    CreatedAtUtc = february20,
                    NextAttemptAtUtc = february20,
                });
            await seed.SaveChangesAsync();
            arborLoanId = arbor.Id;
            arborPaymentId = arborPayment.Id;
            briarLoanId = briar.Id;
            briarPaymentId = briarPayment.Id;
        }

        var loanIds = new[] { arborLoanId, briarLoanId };
        var paymentIds = new[] { arborPaymentId, briarPaymentId };
        async Task<(int Occurrences, int Corrections, decimal[] Balances, int Audits, int Outbox)> SnapshotAsync()
        {
            await using var db = NewContext();
            return (
                await db.LoanPayments.CountAsync(row =>
                    loanIds.Contains(row.LoanId) && row.PeriodKey == "2027-02"),
                await db.LoanPaymentCorrections.CountAsync(row =>
                    paymentIds.Contains(row.LoanPaymentId)),
                await db.Loans
                    .Where(row => loanIds.Contains(row.Id))
                    .OrderBy(row => row.Id)
                    .Select(row => row.CurrentBalance)
                    .ToArrayAsync(),
                await db.AtomicAuditLogs.CountAsync(row =>
                    row.PortfolioId == _portfolioId),
                await db.OutboxMessages.CountAsync(row =>
                    row.PortfolioId == _portfolioId));
        }

        var before = await SnapshotAsync();
        Recorder.Clear();
        await using (var runScope = _services!.CreateAsyncScope())
        {
            var runDb = runScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var service = new DebtServiceService(
                runScope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>(),
                runDb,
                new FixedTimeProvider(february20),
                new FixedTimeZoneProvider(TimeZoneInfo.Utc),
                new ScheduledAutomationClaimStore(runDb),
                NullLogger<DebtServiceService>.Instance);

            (await service.GenerateAsync()).Should().Be(0);
        }
        Recorder.Commands.Should().NotContain(sql =>
                sql.Contains("FROM \"LoanPayments\"", StringComparison.Ordinal) &&
                sql.Contains("\"LoanPaymentCorrections\"", StringComparison.Ordinal),
            "an already-corrected February schedule must not enter the Atomic Engine tail reader");
        var after = await SnapshotAsync();

        before.Occurrences.Should().Be(2);
        after.Occurrences.Should().Be(before.Occurrences);
        before.Corrections.Should().Be(2);
        after.Corrections.Should().Be(before.Corrections);
        before.Balances.Should().Equal(124_963m, 130_016m);
        after.Balances.Should().Equal(before.Balances);
        before.Audits.Should().Be(4);
        after.Audits.Should().Be(before.Audits);
        before.Outbox.Should().Be(4);
        after.Outbox.Should().Be(before.Outbox);
    }

    [SkippableFact]
    public async Task RecurringExpense_BoundedCatchUpLeavesEveryOlderOccurrenceRecoverable()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today.AddMonths(-40));
        var firstClaim = await ClaimExpenseAsync();

        var first = await ExecuteAtomicAsync(
            ExpenseIdentity(firstClaim.ClaimToken, "bounded-first"),
            ExpenseCommand(firstClaim),
            ExpenseCodec);

        first.Value.GeneratedRowCount.Should().Be(36);
        var secondClaim = await ClaimExpenseAsync();
        var second = await ExecuteAtomicAsync(
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
        var firstClaim = (await ClaimDebtAsync()).Single();
        var first = await ExecuteAtomicAsync(
            DebtIdentity(firstClaim.ClaimToken, "bounded-first"),
            new ApplyClaimedDebtServiceBatchCommand(
                [firstClaim.Id], firstClaim.ClaimToken, _today, _today.AddMinutes(1)),
            DebtCodec);

        first.Value.GeneratedRowCount.Should().Be(36);
        var secondClaim = (await ClaimDebtAsync()).Single();
        var second = await ExecuteAtomicAsync(
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
    public async Task DebtService_ImportedHistoricalLoanStartsAtFirstDueDateAfterImport_AndUsesCurrentBalance()
    {
        SkipIfNoDocker();
        var importDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var firstDueDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var importedBalance = 125_825m;
        var loanId = await SeedLoanAsync(
            new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            createdAt: importDate,
            currentBalance: importedBalance,
            dayOfMonthDue: 12,
            monthlyPrincipalInterest: 900m);

        (await ClaimDebtAsync(importDate)).Should().BeEmpty();
        var claim = (await ClaimDebtAsync(firstDueDate)).Single();
        var result = await ExecuteAtomicAsync(
            DebtIdentity(claim.ClaimToken, "imported-current-balance"),
            new ApplyClaimedDebtServiceBatchCommand(
                [claim.Id], claim.ClaimToken, firstDueDate, firstDueDate.AddMinutes(1)),
            DebtCodec);

        result.Value.GeneratedRowCount.Should().Be(1);
        await using var verify = NewContext();
        var payment = await verify.LoanPayments.SingleAsync(row => row.LoanId == loanId);
        payment.PeriodKey.Should().Be("2027-01");
        payment.DueDate.Should().Be(firstDueDate);
        payment.BalanceAfter.Should().BeLessThan(importedBalance);
        payment.BalanceAfter.Should().BeGreaterThan(123_999.58m);
        var loan = await verify.Loans.SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(importedBalance,
            "a scheduled projection must not reduce the live balance");
    }

    [SkippableFact]
    public async Task DebtService_ImportedHistoricalLoanCreatedAfterDueDayStartsNextMonth_AndUsesCurrentBalance()
    {
        SkipIfNoDocker();
        var importDate = new DateTime(2027, 1, 13, 0, 0, 0, DateTimeKind.Utc);
        var nextDueDate = new DateTime(2027, 2, 12, 0, 0, 0, DateTimeKind.Utc);
        var importedBalance = 125_825m;
        var loanId = await SeedLoanAsync(
            new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            createdAt: importDate,
            currentBalance: importedBalance,
            dayOfMonthDue: 12,
            monthlyPrincipalInterest: 900m);

        (await ClaimDebtAsync(importDate)).Should().BeEmpty();
        var claim = (await ClaimDebtAsync(nextDueDate)).Single();
        var result = await ExecuteAtomicAsync(
            DebtIdentity(claim.ClaimToken, "imported-after-due-day"),
            new ApplyClaimedDebtServiceBatchCommand(
                [claim.Id], claim.ClaimToken, nextDueDate, nextDueDate.AddMinutes(1)),
            DebtCodec);

        result.Value.GeneratedRowCount.Should().Be(1);
        await using var verify = NewContext();
        var payment = await verify.LoanPayments.SingleAsync(row => row.LoanId == loanId);
        payment.PeriodKey.Should().Be("2027-02");
        payment.DueDate.Should().Be(nextDueDate);
        payment.BalanceAfter.Should().BeLessThan(importedBalance);
        payment.BalanceAfter.Should().BeGreaterThan(123_999.58m);
        var loan = await verify.Loans.SingleAsync(row => row.Id == loanId);
        loan.CurrentBalance.Should().Be(importedBalance,
            "a scheduled projection must not reduce the live balance");
    }

    [SkippableFact]
    public async Task DebtService_ImportedHistoricalLoanWithPreImportTailDoesNotBackfillHistoricalPeriods_AndUsesCurrentBalance()
    {
        SkipIfNoDocker();
        var importDate = new DateTime(2027, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        var firstDueDate = new DateTime(2027, 1, 12, 0, 0, 0, DateTimeKind.Utc);
        var importedBalance = 125_825m;
        var preImportTailBalance = 50_000m;
        var databaseWallClockCreatedAt = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var loanId = await SeedLoanAsync(
            new DateTime(2017, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            createdAt: databaseWallClockCreatedAt,
            updatedAt: importDate,
            debtServiceAutomationStartDate: importDate,
            currentBalance: importedBalance,
            dayOfMonthDue: 12,
            monthlyPrincipalInterest: 900m);
        await SeedLoanPaymentAsync(
            loanId,
            "2026-07",
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            preImportTailBalance,
            importDate);

        (await ClaimDebtAsync(importDate)).Should().BeEmpty();
        (await ClaimDebtAsync(importDate)).Should().BeEmpty();

        var claim = (await ClaimDebtAsync(firstDueDate)).Single();
        var result = await ExecuteAtomicAsync(
            DebtIdentity(claim.ClaimToken, "imported-pre-import-tail"),
            new ApplyClaimedDebtServiceBatchCommand(
                [claim.Id], claim.ClaimToken, firstDueDate, firstDueDate.AddMinutes(1)),
            DebtCodec);

        result.Value.GeneratedRowCount.Should().Be(1);
        await using var verify = NewContext();
        var periods = await verify.LoanPayments
            .Where(row => row.LoanId == loanId)
            .OrderBy(row => row.PeriodKey)
            .Select(row => row.PeriodKey)
            .ToListAsync();
        periods.Should().Equal("2026-07", "2027-01");
        periods.Should().NotContain(period => string.CompareOrdinal(period, "2026-08") >= 0
            && string.CompareOrdinal(period, "2026-12") <= 0);
        var generated = await verify.LoanPayments.SingleAsync(row => row.LoanId == loanId && row.PeriodKey == "2027-01");
        generated.DueDate.Should().Be(firstDueDate);
        generated.BalanceAfter.Should().BeLessThan(importedBalance);
        generated.BalanceAfter.Should().BeGreaterThan(123_999.58m);
        generated.BalanceAfter.Should().BeGreaterThan(preImportTailBalance);
        var loan = await verify.Loans.SingleAsync(row => row.Id == loanId);
        loan.CreatedAt.Should().Be(databaseWallClockCreatedAt);
        loan.DebtServiceAutomationStartDate.Should().Be(importDate);
        loan.CurrentBalance.Should().Be(importedBalance,
            "a scheduled projection must not reduce the live balance");
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

        await FluentActions.Invoking(() => ExecuteAtomicAsync(
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

        (await ExecuteAtomicAsync(
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

        var failureAssertion = await FluentActions.Invoking(() => ExecuteAtomicAsync(
                identity, ExpenseCommand(claim), ExpenseCodec))
            .Should().ThrowAsync<DbUpdateException>();
        failureAssertion.Which.InnerException.Should().BeOfType<InvalidOperationException>();
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

        (await ExecuteAtomicAsync(identity, ExpenseCommand(claim), ExpenseCodec))
            .Value.GeneratedRowCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task DifferentOperationKey_CannotDuplicateRecurringScheduleOccurrence()
    {
        SkipIfNoDocker();
        var templateId = await SeedRecurringExpenseAsync(_today);
        var firstClaim = await ClaimExpenseAsync();
        await ExecuteAtomicAsync(
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

        await FluentActions.Invoking(() => ExecuteAtomicAsync(
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

    [SkippableFact]
    public async Task RecurringMaintenance_ReplayReturnsOneWorkOrderAndOneScheduleAdvance()
    {
        SkipIfNoDocker();
        var taskId = await SeedRecurringMaintenanceAsync(_today.AddDays(-8));
        var claim = await ClaimMaintenanceAsync();
        var identity = new AtomicCommandIdentity(
            "scheduled-automation.recurring-maintenance.apply", $"{claim.ClaimToken:N}:{claim.Id}");
        var command = new ApplyClaimedRecurringMaintenanceBatchCommand(
            [claim.Id], claim.ClaimToken, _today, _today.AddMinutes(1), "America/New_York");

        var first = await ExecuteAtomicAsync(identity, command, MaintenanceCodec);
        var replay = await ExecuteAtomicAsync(identity, command, MaintenanceCodec);

        replay.Value.Should().BeEquivalentTo(first.Value);
        await using var verify = NewContext();
        (await verify.WorkOrders.CountAsync(row => row.RecurringMaintenanceTaskId == taskId)).Should().Be(1);
        var task = await verify.RecurringMaintenanceTasks.SingleAsync(row => row.Id == taskId);
        task.NextDueDate.Should().BeAfter(_today);
        task.WorkerClaimToken.Should().BeNull();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    private async Task<int> SeedRecurringExpenseAsync(DateTime nextRunDate)
    {
        await using var db = NewContext();
        var template = new RecurringExpense
        {
            PortfolioId = _portfolioId,
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

    private async Task<int> SeedLoanAsync(
        DateTime? startDate = null,
        DateTime? createdAt = null,
        DateTime? updatedAt = null,
        DateTime? debtServiceAutomationStartDate = null,
        decimal currentBalance = 100_000m,
        int? dayOfMonthDue = null,
        decimal monthlyPrincipalInterest = 600m)
    {
        await using var db = NewContext();
        var effectiveStartDate = startDate ?? _today;
        var effectiveCreatedAt = createdAt ?? effectiveStartDate;
        var loan = new Loan
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            Lender = "Atomic Bank",
            OriginalAmount = 100_000m,
            CurrentBalance = currentBalance,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = effectiveStartDate,
            DebtServiceAutomationStartDate = debtServiceAutomationStartDate ?? effectiveCreatedAt,
            DayOfMonthDue = dayOfMonthDue ?? _today.Day,
            MonthlyPrincipalInterest = monthlyPrincipalInterest,
            Status = LoanStatus.Active,
            CreatedAt = effectiveCreatedAt,
            UpdatedAt = updatedAt ?? effectiveCreatedAt,
        };
        db.Loans.Add(loan);
        await db.SaveChangesAsync();
        return loan.Id;
    }

    private async Task SeedLoanPaymentAsync(
        int loanId,
        string periodKey,
        DateTime dueDate,
        decimal balanceAfter,
        DateTime createdAt)
    {
        await using var db = NewContext();
        db.LoanPayments.Add(new LoanPayment
        {
            PortfolioId = _portfolioId,
            LoanId = loanId,
            PeriodKey = periodKey,
            DueDate = dueDate,
            InterestAmount = 100m,
            PrincipalAmount = 500m,
            TotalAmount = 600m,
            BalanceAfter = balanceAfter,
            Status = LoanPaymentStatus.Scheduled,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedRecurringMaintenanceAsync(DateTime nextDueDate)
    {
        await using var db = NewContext();
        var task = new RecurringMaintenanceTask
        {
            PortfolioId = _portfolioId,
            PropertyId = _propertyId,
            Title = "HVAC filter",
            Description = "Replace HVAC filter",
            Category = "HVAC",
            RecurrenceInterval = RecurrenceInterval.Weekly,
            NextDueDate = nextDueDate,
            ScheduledTime = new TimeOnly(14, 30),
            IsActive = true,
            Priority = WorkOrderPriority.Normal,
            CreatedAt = _today,
            UpdatedAt = _today,
        };
        db.RecurringMaintenanceTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    private async Task<ScheduledAutomationClaim> ClaimExpenseAsync()
    {
        await using var db = NewContext();
        return (await new ScheduledAutomationClaimStore(db).ClaimRecurringExpensesAsync(
            "expense-test", _today, TimeSpan.FromMinutes(5), 1)).Single();
    }

    private async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtAsync(DateTime? today = null)
    {
        await using var db = NewContext();
        return await new ScheduledAutomationClaimStore(db).ClaimDebtServiceAsync(
            "debt-test", today ?? _today, TimeSpan.FromMinutes(5), 1);
    }

    private async Task<ScheduledAutomationClaim> ClaimMaintenanceAsync()
    {
        await using var db = NewContext();
        return (await new ScheduledAutomationClaimStore(db).ClaimRecurringMaintenanceAsync(
            "maintenance-test", _today, TimeSpan.FromMinutes(5), 1)).Single();
    }

    private ApplyClaimedRecurringExpenseBatchCommand ExpenseCommand(ScheduledAutomationClaim claim) =>
        new([claim.Id], claim.ClaimToken, _today, _today.AddMinutes(1));

    private static AtomicCommandIdentity ExpenseIdentity(Guid token, string suffix) =>
        new("scheduled-finance.recurring-expense.apply", $"{token:N}:{suffix}");

    private static AtomicCommandIdentity DebtIdentity(Guid token, string suffix) =>
        new("scheduled-finance.debt-service.apply", $"{token:N}:{suffix}");

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = command switch
        {
            ApplyClaimedDebtServiceBatchCommand debt =>
                (TransactionalWrite<TCommand, TResult>)(object)ScheduledFinanceWriteSupport.Write(
                    debt,
                    new ApplyClaimedDebtServiceBatchRule(db).ExecuteAsync,
                    new ApplyClaimedDebtServiceBatchRule(db).AuthorizeAsync),
            ApplyClaimedRecurringExpenseBatchCommand expense =>
                (TransactionalWrite<TCommand, TResult>)(object)ScheduledFinanceWriteSupport.Write(
                    expense,
                    new ApplyClaimedRecurringExpenseBatchRule(db).ExecuteAsync,
                    new ApplyClaimedRecurringExpenseBatchRule(db).AuthorizeAsync),
            ApplyClaimedRecurringMaintenanceBatchCommand maintenance =>
                (TransactionalWrite<TCommand, TResult>)(object)ScheduledFinanceWriteSupport.Write(
                    maintenance,
                    new ApplyClaimedRecurringMaintenanceBatchRule(db).ExecuteAsync,
                    new ApplyClaimedRecurringMaintenanceBatchRule(db).AuthorizeAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        write.OperationName.Should().Be(identity.CommandType);
        write.ResultContract.Should().Be(codec.ContractName);
        return await scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, write);
    }

    private async Task SeedLegacyReceiptAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        TResult result)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var db = NewContext();
        db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            CommandType = identity.CommandType,
            IdempotencyKey = identity.IdempotencyKey,
            RequestFingerprint = AtomicCommandFingerprint.Create(command),
            Status = AtomicCommandReceiptStatus.Completed,
            ResultContract = codec.ContractName,
            ResultJson = codec.Serialize(result),
            StartedAt = _today,
            CompletedAt = _today,
        });
        await db.SaveChangesAsync();
    }

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

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone { get; } = timeZone;
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
