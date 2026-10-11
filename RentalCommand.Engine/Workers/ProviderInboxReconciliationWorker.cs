using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

public sealed class ProviderInboxReconciliationWorker : EngineWorkerBase
{
    protected override string WorkerName => "ProviderInboxReconciliationWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(30);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public ProviderInboxReconciliationWorker(
        IServiceProvider serviceProvider,
        ILogger<ProviderInboxReconciliationWorker> logger)
        : base(serviceProvider, logger)
    {
    }

    protected override Task<int> ExecuteCycleAsync(
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken) =>
        scopedProvider.GetRequiredService<ProviderInboxReconciliationService>()
            .ReconcileAsync(cancellationToken);
}
