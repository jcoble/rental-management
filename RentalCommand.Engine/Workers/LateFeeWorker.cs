using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Polls every 6 hours to assess late fees on overdue rent payments via <see cref="ILateFeeService"/>.
/// Controlled per workspace by <c>AutomationSettings.EnableLateFees</c>; disabled workspaces
/// produce no eligible rows and are not touched.
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
