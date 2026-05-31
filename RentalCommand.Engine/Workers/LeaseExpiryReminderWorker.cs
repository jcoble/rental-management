using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Polls every 6 hours to send one-time lease-expiry reminders to property owners for
/// active leases expiring within the configured look-ahead window.
/// Delegates all logic to <see cref="ILeaseExpiryReminderService"/>.
/// </summary>
public sealed class LeaseExpiryReminderWorker : EngineWorkerBase
{
    protected override string WorkerName => "LeaseExpiryReminderWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromHours(6);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public LeaseExpiryReminderWorker(IServiceProvider serviceProvider, ILogger<LeaseExpiryReminderWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var svc = scoped.GetRequiredService<ILeaseExpiryReminderService>();
        return await svc.RemindAsync(ct);
    }
}
