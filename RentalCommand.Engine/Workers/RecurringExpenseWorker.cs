using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Hourly worker that delegates to <see cref="IRecurringExpenseGenerationService"/> to materialize
/// due recurring-expense templates into expense rows. Mirrors <see cref="RecurringMaintenanceWorker"/>;
/// idempotent by schedule advancement.
/// </summary>
public sealed class RecurringExpenseWorker : EngineWorkerBase
{
    protected override string WorkerName => "RecurringExpenseWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public RecurringExpenseWorker(IServiceProvider serviceProvider, ILogger<RecurringExpenseWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<IRecurringExpenseGenerationService>();
        return await svc.GenerateAsync(ct);
    }
}
