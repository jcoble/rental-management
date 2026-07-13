using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>Materializes due canonical tenant-notice work before the claimed draft worker consumes it.</summary>
public sealed class TenantNoticeCandidateWorker : EngineWorkerBase
{
    protected override string WorkerName => "TenantNoticeCandidateWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(6);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public TenantNoticeCandidateWorker(
        IServiceProvider serviceProvider,
        ILogger<TenantNoticeCandidateWorker> logger)
        : base(serviceProvider, logger) { }

    protected override Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct) =>
        scoped.GetRequiredService<ITenantNoticeCandidateGenerationService>().GenerateDueAsync(ct);
}
