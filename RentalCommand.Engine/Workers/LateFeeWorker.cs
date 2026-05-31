using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Polls every 6 hours to assess late fees on overdue rent payments via <see cref="ILateFeeService"/>.
/// Controlled by <see cref="Core.Configuration.NotificationsConfig.EnableLateFees"/>; when that flag
/// is false the service returns 0 immediately and no records are touched.
/// </summary>
public sealed class LateFeeWorker : EngineWorkerBase
{
    protected override string WorkerName => "LateFeeWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(6);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public LateFeeWorker(IServiceProvider serviceProvider, ILogger<LateFeeWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var service = scopedProvider.GetRequiredService<ILateFeeService>();
        return await service.AssessAsync(cancellationToken);
    }
}
