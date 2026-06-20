using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Hourly worker that delegates to <see cref="IDebtServiceService"/> to generate monthly loan
/// payments (the amortization schedule) for active loans. Mirrors <see cref="RentChargeWorker"/>;
/// idempotent by (LoanId, PeriodKey).
/// </summary>
public sealed class DebtServiceWorker : EngineWorkerBase
{
    protected override string WorkerName => "DebtServiceWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public DebtServiceWorker(IServiceProvider serviceProvider, ILogger<DebtServiceWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<IDebtServiceService>();
        return await svc.GenerateAsync(ct);
    }
}
