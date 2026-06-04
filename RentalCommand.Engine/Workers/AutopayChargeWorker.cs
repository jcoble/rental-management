using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Hourly worker that delegates to <see cref="IAutopayChargeService"/> to charge due rent
/// off-session for leases enrolled in autopay. Runs under the Engine's single-instance advisory
/// lock (acquired in Program.cs); the service is a no-op when Stripe is not configured.
/// </summary>
public sealed class AutopayChargeWorker : EngineWorkerBase
{
    protected override string WorkerName => "AutopayChargeWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public AutopayChargeWorker(IServiceProvider serviceProvider, ILogger<AutopayChargeWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<IAutopayChargeService>();
        return await svc.ChargeDueAsync(ct);
    }
}
