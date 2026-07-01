using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Time;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Maps a <c>SimWorkerCommand</c> key to the matching automation-service call — each resolved from the
/// current Engine scope, so every run inherits the Engine's admin RLS + system audit actor. The
/// <see cref="SimWorkerKeys.RunDue"/> key runs the M4 due-order batch and sums the created counts.
/// Dev-only; registered only when <c>Simulation:Enabled</c>. Method names are hardcoded per service
/// (verified): rent/debt/recurring → <c>GenerateAsync</c>, late-fee → <c>AssessAsync</c>, autopay →
/// <c>ChargeDueAsync</c>, lease-expiry → <c>RemindAsync</c>, notice → <c>GenerateAllAsync</c>,
/// daily-briefing → <c>EnqueueDueAsync(null, ct)</c> (null ⇒ use the injected sim clock).
/// </summary>
public sealed class SimWorkerRegistry
{
    private readonly IReadOnlyDictionary<string, Func<IServiceProvider, CancellationToken, Task<int>>> _invokers;

    public SimWorkerRegistry()
    {
        _invokers = new Dictionary<string, Func<IServiceProvider, CancellationToken, Task<int>>>(StringComparer.Ordinal)
        {
            [SimWorkerKeys.RentCharge]           = (sp, ct) => sp.GetRequiredService<IRentChargeService>().GenerateAsync(ct),
            [SimWorkerKeys.NoticeDraft]          = (sp, ct) => sp.GetRequiredService<INoticeDraftGenerationService>().GenerateAllAsync(ct),
            [SimWorkerKeys.LeaseExpiryReminder]  = (sp, ct) => sp.GetRequiredService<ILeaseExpiryReminderService>().RemindAsync(ct),
            [SimWorkerKeys.LateFee]              = (sp, ct) => sp.GetRequiredService<ILateFeeService>().AssessAsync(ct),
            [SimWorkerKeys.Autopay]              = (sp, ct) => sp.GetRequiredService<IAutopayChargeService>().ChargeDueAsync(ct),
            [SimWorkerKeys.DebtService]          = (sp, ct) => sp.GetRequiredService<IDebtServiceService>().GenerateAsync(ct),
            [SimWorkerKeys.RecurringExpense]     = (sp, ct) => sp.GetRequiredService<IRecurringExpenseGenerationService>().GenerateAsync(ct),
            [SimWorkerKeys.RecurringMaintenance] = (sp, ct) => sp.GetRequiredService<IRecurringMaintenanceService>().GenerateAsync(ct),
            [SimWorkerKeys.DailyBriefing]        = (sp, ct) => sp.GetRequiredService<IDailyBriefingDeliveryService>().EnqueueDueAsync(null, ct),
        };
    }

    /// <summary>True if <paramref name="key"/> resolves to a service or is <see cref="SimWorkerKeys.RunDue"/>.</summary>
    public bool IsKnownKey(string key) => key == SimWorkerKeys.RunDue || _invokers.ContainsKey(key);

    /// <summary>
    /// Runs the command for <paramref name="key"/> against <paramref name="scopedProvider"/> and returns the
    /// total created count. <see cref="SimWorkerKeys.RunDue"/> runs <see cref="SimWorkerKeys.RunDueSequence"/>
    /// in order.
    /// </summary>
    public async Task<int> RunAsync(string key, IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        if (key == SimWorkerKeys.RunDue)
        {
            var total = 0;
            foreach (var stepKey in SimWorkerKeys.RunDueSequence)
                total += await _invokers[stepKey](scopedProvider, cancellationToken);
            return total;
        }

        if (_invokers.TryGetValue(key, out var invoker))
            return await invoker(scopedProvider, cancellationToken);

        throw new InvalidOperationException($"Unknown sim worker key '{key}'.");
    }
}
