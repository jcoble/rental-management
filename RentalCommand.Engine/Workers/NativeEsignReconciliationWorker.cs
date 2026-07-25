using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Completes native e-sign requests left in ExecutionPending after transient PDF or storage failures.
/// Each batch is leased with PostgreSQL SKIP LOCKED; finalization is receipt-backed, aggregate-locked,
/// and fenced by the current execution claim token.
/// </summary>
public sealed class NativeEsignReconciliationWorker : EngineWorkerBase
{
    protected override string WorkerName => "NativeEsignReconciliationWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(5);

    public NativeEsignReconciliationWorker(
        IServiceProvider serviceProvider,
        ILogger<NativeEsignReconciliationWorker> logger)
        : base(serviceProvider, logger)
    {
    }

    protected override Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken) =>
        scopedProvider.GetRequiredService<NativeEsignReconciliationService>()
            .ReconcileAsync(cancellationToken);
}
