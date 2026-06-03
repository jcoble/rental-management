using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

public sealed class DailyBriefingDeliveryWorker : EngineWorkerBase
{
    protected override string WorkerName => "DailyBriefingDeliveryWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public DailyBriefingDeliveryWorker(
        IServiceProvider serviceProvider,
        ILogger<DailyBriefingDeliveryWorker> logger)
        : base(serviceProvider, logger)
    {
    }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var service = scopedProvider.GetRequiredService<IDailyBriefingDeliveryService>();
        return await service.EnqueueDueAsync(null, cancellationToken);
    }
}
