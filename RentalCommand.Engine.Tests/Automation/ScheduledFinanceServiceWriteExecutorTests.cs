using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Automation;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class ScheduledFinanceServiceWriteExecutorTests
{
    private static readonly DateTime BusinessNowUtc =
        new(2027, 2, 20, 9, 15, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DebtService_PreservesClaimTokenStepKeyAndContract()
    {
        var token = Guid.Parse("3999e57b-6ba1-4e2c-8c65-93f4001d1f04");
        var claims = ClaimStore(store => store.Setup(item => item.ClaimDebtServiceAsync(
                It.IsAny<string>(), BusinessNowUtc.Date, TimeSpan.FromMinutes(3), 25,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScheduledAutomationClaim(41, 7, token)]));
        var writes = new CapturingJobStepWriteExecutor(
            new ApplyScheduledFinanceBatchResult(ScheduledFinanceApplyOutcome.Applied, 1, 2));
        var service = new DebtServiceService(
            writes, NewDb(), new FixedTimeProvider(BusinessNowUtc), UtcTimeZone(), claims,
            NullLogger<DebtServiceService>.Instance);

        (await service.GenerateAsync()).Should().Be(2);

        writes.StepKey.Should().Be(token.ToString("N"));
        writes.OperationName.Should().Be("scheduled-finance.debt-service.apply");
        writes.ResultContract.Should().Be("scheduled-finance.debt-service.apply.v1");
        var command = writes.Command.Should().BeOfType<ApplyClaimedDebtServiceBatchCommand>().Subject;
        command.LoanIds.Should().Equal(41);
        command.ClaimToken.Should().Be(token);
        command.BusinessDateUtc.Should().Be(BusinessNowUtc.Date);
    }

    [Fact]
    public async Task RecurringExpense_PreservesClaimTokenStepKeyAndContract()
    {
        var token = Guid.Parse("4f43947f-c2db-4e39-a94d-f19277830bc6");
        var claims = ClaimStore(store => store.Setup(item => item.ClaimRecurringExpensesAsync(
                It.IsAny<string>(), BusinessNowUtc.Date, TimeSpan.FromMinutes(3), 25,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScheduledAutomationClaim(52, 7, token)]));
        var writes = new CapturingJobStepWriteExecutor(
            new ApplyScheduledFinanceBatchResult(ScheduledFinanceApplyOutcome.Applied, 1, 3));
        var service = new RecurringExpenseGenerationService(
            writes, NewDb(), new FixedTimeProvider(BusinessNowUtc), UtcTimeZone(), claims,
            NullLogger<RecurringExpenseGenerationService>.Instance);

        (await service.GenerateAsync()).Should().Be(3);

        writes.StepKey.Should().Be(token.ToString("N"));
        writes.OperationName.Should().Be("scheduled-finance.recurring-expense.apply");
        writes.ResultContract.Should().Be("scheduled-finance.recurring-expense.apply.v1");
        var command = writes.Command.Should().BeOfType<ApplyClaimedRecurringExpenseBatchCommand>().Subject;
        command.RecurringExpenseIds.Should().Equal(52);
        command.ClaimToken.Should().Be(token);
        command.BusinessDateUtc.Should().Be(BusinessNowUtc.Date);
    }

    [Fact]
    public async Task RecurringMaintenance_PreservesClaimAndScheduleStepKeyAndContract()
    {
        var token = Guid.Parse("30411bbc-530f-4eef-be4b-30acd3ce28d4");
        var claims = ClaimStore(store => store.Setup(item => item.ClaimRecurringMaintenanceAsync(
                It.IsAny<string>(), BusinessNowUtc.Date, TimeSpan.FromMinutes(6), 25,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScheduledAutomationClaim(63, 7, token)]));
        var writes = new CapturingJobStepWriteExecutor(
            new ApplyScheduledFinanceBatchResult(ScheduledFinanceApplyOutcome.Applied, 1, 1));
        var services = new ServiceCollection()
            .AddSingleton<IJobStepWriteExecutor>(writes)
            .AddScoped(_ => NewDb())
            .BuildServiceProvider();
        var service = new RecurringMaintenanceService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTimeProvider(BusinessNowUtc), UtcTimeZone(), claims,
            NullLogger<RecurringMaintenanceService>.Instance);

        (await service.GenerateAsync()).Should().Be(1);

        writes.StepKey.Should().Be($"{token:N}:63");
        writes.OperationName.Should().Be("scheduled-automation.recurring-maintenance.apply");
        writes.ResultContract.Should().Be("scheduled-automation.recurring-maintenance.apply.v1");
        var command = writes.Command.Should().BeOfType<ApplyClaimedRecurringMaintenanceBatchCommand>().Subject;
        command.RecurringMaintenanceTaskIds.Should().Equal(63);
        command.ClaimToken.Should().Be(token);
        command.BusinessTimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
    }

    [Fact]
    public async Task RecurringTenantCharge_UsesRunTokenStepKeyAndLegacyContract()
    {
        var writes = new CapturingJobStepWriteExecutor(new ApplyRecurringTenantChargeBatchResult(4));
        var service = new RecurringTenantChargeGenerationService(
            writes, NewDb(), new FixedTimeProvider(BusinessNowUtc),
            NullLogger<RecurringTenantChargeGenerationService>.Instance);

        (await service.GenerateAsync()).Should().Be(4);

        writes.OperationName.Should().Be("scheduled-finance.recurring-tenant-charge.apply");
        writes.ResultContract.Should().Be("scheduled-finance.recurring-tenant-charge.apply.v1");
        var command = writes.Command.Should().BeOfType<ApplyRecurringTenantChargeBatchCommand>().Subject;
        writes.StepKey.Should().Be(command.RunToken.ToString("N"));
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.BatchSize.Should().Be(200);
    }

    private static IScheduledAutomationClaimStore ClaimStore(
        Action<Mock<IScheduledAutomationClaimStore>> setup)
    {
        var store = new Mock<IScheduledAutomationClaimStore>(MockBehavior.Strict);
        setup(store);
        return store.Object;
    }

    private static RentalCommandDbContext NewDb() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    private static IAppTimeZoneProvider UtcTimeZone() => new FixedTimeZoneProvider(TimeZoneInfo.Utc);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone { get; } = timeZone;
    }
}
