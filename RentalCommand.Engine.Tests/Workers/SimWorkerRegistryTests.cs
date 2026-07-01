using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Core.Time;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Workers;

public class SimWorkerRegistryTests
{
    [Fact]
    public void RunDueSequence_IsInM4DependencyOrder()
    {
        SimWorkerKeys.RunDueSequence.Should().Equal(
            "rent-charge", "notice-draft", "lease-expiry-reminder", "late-fee",
            "autopay", "debt-service", "recurring-expense", "recurring-maintenance");

        // daily-briefing is intentionally NOT part of run-due.
        SimWorkerKeys.RunDueSequence.Should().NotContain(SimWorkerKeys.DailyBriefing);
    }

    [Theory]
    [InlineData(SimWorkerKeys.RentCharge)]
    [InlineData(SimWorkerKeys.NoticeDraft)]
    [InlineData(SimWorkerKeys.LeaseExpiryReminder)]
    [InlineData(SimWorkerKeys.LateFee)]
    [InlineData(SimWorkerKeys.Autopay)]
    [InlineData(SimWorkerKeys.DebtService)]
    [InlineData(SimWorkerKeys.RecurringExpense)]
    [InlineData(SimWorkerKeys.RecurringMaintenance)]
    [InlineData(SimWorkerKeys.DailyBriefing)]
    public async Task EachKey_InvokesOnlyTheMatchingService(string key)
    {
        var callOrder = new List<string>();
        using var provider = BuildProvider(callOrder);
        var registry = new SimWorkerRegistry();

        var created = await registry.RunAsync(key, provider, CancellationToken.None);

        callOrder.Should().Equal(key);          // exactly one service — the right one — ran
        created.Should().Be(CountFor(key));      // and its created count is returned
    }

    [Fact]
    public async Task RunDue_InvokesDueServicesInExactOrder_AndSumsCounts()
    {
        var callOrder = new List<string>();
        using var provider = BuildProvider(callOrder);
        var registry = new SimWorkerRegistry();

        var total = await registry.RunAsync(SimWorkerKeys.RunDue, provider, CancellationToken.None);

        callOrder.Should().Equal(SimWorkerKeys.RunDueSequence);           // M4 order, daily-briefing excluded
        total.Should().Be(SimWorkerKeys.RunDueSequence.Sum(CountFor));
    }

    [Fact]
    public async Task UnknownKey_Throws()
    {
        var registry = new SimWorkerRegistry();
        using var provider = BuildProvider(new List<string>());

        var act = () => registry.RunAsync("not-a-worker", provider, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // Distinct created count per key so the run-due sum is unambiguous.
    private static int CountFor(string key) => key switch
    {
        SimWorkerKeys.RentCharge => 1,
        SimWorkerKeys.NoticeDraft => 2,
        SimWorkerKeys.LeaseExpiryReminder => 3,
        SimWorkerKeys.LateFee => 4,
        SimWorkerKeys.Autopay => 5,
        SimWorkerKeys.DebtService => 6,
        SimWorkerKeys.RecurringExpense => 7,
        SimWorkerKeys.RecurringMaintenance => 8,
        SimWorkerKeys.DailyBriefing => 9,
        _ => 0,
    };

    private static ServiceProvider BuildProvider(List<string> order)
    {
        var services = new ServiceCollection();

        var rentCharge = new Mock<IRentChargeService>();
        rentCharge.Setup(s => s.GenerateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.RentCharge))
            .ReturnsAsync(CountFor(SimWorkerKeys.RentCharge));
        services.AddSingleton(rentCharge.Object);

        var notice = new Mock<INoticeDraftGenerationService>();
        notice.Setup(s => s.GenerateAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.NoticeDraft))
            .ReturnsAsync(CountFor(SimWorkerKeys.NoticeDraft));
        services.AddSingleton(notice.Object);

        var leaseExpiry = new Mock<ILeaseExpiryReminderService>();
        leaseExpiry.Setup(s => s.RemindAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.LeaseExpiryReminder))
            .ReturnsAsync(CountFor(SimWorkerKeys.LeaseExpiryReminder));
        services.AddSingleton(leaseExpiry.Object);

        var lateFee = new Mock<ILateFeeService>();
        lateFee.Setup(s => s.AssessAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.LateFee))
            .ReturnsAsync(CountFor(SimWorkerKeys.LateFee));
        services.AddSingleton(lateFee.Object);

        var autopay = new Mock<IAutopayChargeService>();
        autopay.Setup(s => s.ChargeDueAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.Autopay))
            .ReturnsAsync(CountFor(SimWorkerKeys.Autopay));
        services.AddSingleton(autopay.Object);

        var debtService = new Mock<IDebtServiceService>();
        debtService.Setup(s => s.GenerateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.DebtService))
            .ReturnsAsync(CountFor(SimWorkerKeys.DebtService));
        services.AddSingleton(debtService.Object);

        var recurringExpense = new Mock<IRecurringExpenseGenerationService>();
        recurringExpense.Setup(s => s.GenerateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.RecurringExpense))
            .ReturnsAsync(CountFor(SimWorkerKeys.RecurringExpense));
        services.AddSingleton(recurringExpense.Object);

        var recurringMaintenance = new Mock<IRecurringMaintenanceService>();
        recurringMaintenance.Setup(s => s.GenerateAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.RecurringMaintenance))
            .ReturnsAsync(CountFor(SimWorkerKeys.RecurringMaintenance));
        services.AddSingleton(recurringMaintenance.Object);

        var dailyBriefing = new Mock<IDailyBriefingDeliveryService>();
        dailyBriefing.Setup(s => s.EnqueueDueAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(SimWorkerKeys.DailyBriefing))
            .ReturnsAsync(CountFor(SimWorkerKeys.DailyBriefing));
        services.AddSingleton(dailyBriefing.Object);

        return services.BuildServiceProvider();
    }
}
