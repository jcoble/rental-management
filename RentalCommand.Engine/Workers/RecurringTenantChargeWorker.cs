using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Hourly worker that materializes due recurring tenant-charge schedules.
/// </summary>
public sealed class RecurringTenantChargeWorker : EngineWorkerBase
{
    protected override string WorkerName => "RecurringTenantChargeWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public RecurringTenantChargeWorker(
        IServiceProvider serviceProvider,
        ILogger<RecurringTenantChargeWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(
        IServiceProvider scoped,
        CancellationToken ct)
    {
        var service = scoped.GetRequiredService<IRecurringTenantChargeGenerationService>();
        return await service.GenerateAsync(ct);
    }
}
