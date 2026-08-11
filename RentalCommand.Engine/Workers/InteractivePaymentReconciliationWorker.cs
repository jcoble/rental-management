using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>Durable one-minute production owner for unresolved interactive provider attempts.</summary>
public sealed class InteractivePaymentReconciliationWorker : EngineWorkerBase
{
    private readonly InteractivePaymentReconciliationOptions _options;

    public InteractivePaymentReconciliationWorker(
        IServiceProvider serviceProvider,
        IOptions<InteractivePaymentReconciliationOptions> options,
        ILogger<InteractivePaymentReconciliationWorker> logger)
        : base(serviceProvider, logger) =>
        _options = options.Value;

    protected override string WorkerName => nameof(InteractivePaymentReconciliationWorker);
    protected override TimeSpan PollInterval => _options.PollInterval;
    protected override TimeSpan StepTimeout => _options.StepTimeout;

    protected override Task<int> ExecuteCycleAsync(
        IServiceProvider scopedProvider, CancellationToken cancellationToken) =>
        scopedProvider.GetRequiredService<InteractivePaymentReconciliationService>()
            .ReconcileAsync(cancellationToken);
}
