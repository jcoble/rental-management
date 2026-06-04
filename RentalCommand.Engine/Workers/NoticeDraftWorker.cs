using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Lease Lifecycle Autopilot worker. Polls twice a day to proactively generate notice drafts
/// (renewal offers, escalating late-rent notices, move-out reminders) across all portfolios via
/// <see cref="INoticeDraftGenerationService"/>, so drafts wait for one-tap approval WITHOUT the
/// landlord tapping "Generate". Idempotent (the underlying generation skips notice types that
/// already have an open draft) and gated behind <c>Notifications:EnableNoticeAutopilot</c>.
/// Drafts are only created here — never sent — so auto-generation is safe.
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
