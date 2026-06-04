using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Daily worker that delegates to <see cref="IRecurringMaintenanceService"/> to turn due
/// recurring-maintenance chores into work orders so they surface in the field queue.
/// </summary>
public sealed class RecurringMaintenanceWorker : EngineWorkerBase
{
    protected override string WorkerName => "RecurringMaintenanceWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(24);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(5);

    public RecurringMaintenanceWorker(IServiceProvider serviceProvider, ILogger<RecurringMaintenanceWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<IRecurringMaintenanceService>();
        return await svc.GenerateAsync(ct);
    }
}
