using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Hourly worker that delegates to <see cref="IRentChargeService"/> to generate scheduled rent
/// payments for active leases within the configured lead window.
/// </summary>
public sealed class RentChargeWorker : EngineWorkerBase
{
    protected override string WorkerName => "RentChargeWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public RentChargeWorker(IServiceProvider serviceProvider, ILogger<RentChargeWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<IRentChargeService>();
        return await svc.GenerateAsync(ct);
    }
}
