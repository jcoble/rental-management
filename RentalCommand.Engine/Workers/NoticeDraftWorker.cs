using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Claims DB-selected tenant-notice work and applies each automation's independent mode. Draft mode
/// leaves editable copy for review; Auto freezes the selected template version and queues only the
/// policy's tenant channels after recipient and legal gates pass. Off policies are never claimable.
/// Claims are fenced and idempotent, and failure behavior decides retry, retained draft, or block.
/// </summary>
public sealed class NoticeDraftWorker : EngineWorkerBase
{
    protected override string WorkerName => "NoticeDraftWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(12);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(5);

    public NoticeDraftWorker(IServiceProvider serviceProvider, ILogger<NoticeDraftWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<INoticeDraftGenerationService>();
        return await svc.GenerateAllAsync(ct);
    }
}
