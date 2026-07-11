using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

public sealed class PendingFileUploadCleanupWorker : EngineWorkerBase
{
    protected override string WorkerName => nameof(PendingFileUploadCleanupWorker);
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(15);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public PendingFileUploadCleanupWorker(
        IServiceProvider serviceProvider,
        ILogger<PendingFileUploadCleanupWorker> logger)
        : base(serviceProvider, logger) { }

    protected override Task<int> ExecuteCycleAsync(
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken) =>
        scopedProvider.GetRequiredService<PendingFileUploadCleanupService>()
            .RunBatchAsync(cancellationToken);
}
