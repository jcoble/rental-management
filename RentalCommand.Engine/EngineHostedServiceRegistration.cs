using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine;

internal static class EngineHostedServiceRegistration
{
    internal static IServiceCollection AddEngineHostedServices(
        this IServiceCollection services,
        bool simulationEnabled,
        bool commandBridgeOnly)
    {
        if (commandBridgeOnly && !simulationEnabled)
        {
            throw new InvalidOperationException(
                "Command-bridge-only Engine hosting requires simulation mode.");
        }

        services.AddSingleton<ScanProcessingWorker>();
        services.AddSingleton<IScanProcessingCycleService>(sp =>
            sp.GetRequiredService<ScanProcessingWorker>());

        if (!commandBridgeOnly)
        {
            services.AddHostedService(sp =>
                sp.GetRequiredService<RealtimeInvalidationQueue>());
            services.AddHostedService<OutboxDispatchWorker>();
            services.AddHostedService(sp =>
                sp.GetRequiredService<ScanProcessingWorker>());
            services.AddHostedService<RentChargeWorker>();
            services.AddHostedService<DebtServiceWorker>();
            services.AddHostedService<RecurringExpenseWorker>();
            services.AddHostedService<AutopayChargeWorker>();
            services.AddHostedService<RecurringMaintenanceWorker>();
            services.AddHostedService<LateFeeWorker>();
            services.AddHostedService<TenantNoticeCandidateWorker>();
            services.AddHostedService<DailyBriefingDeliveryWorker>();
            services.AddHostedService<NoticeDraftWorker>();
            services.AddHostedService<NativeEsignReconciliationWorker>();
            services.AddHostedService<ProviderInboxReconciliationWorker>();
            services.AddHostedService<PendingFileUploadCleanupWorker>();
            services.AddHostedService<AccountingPullWorker>();
            services.AddHostedService<AccountingTokenRefreshWorker>();
        }

        // Dev-only command bridge. Its registry resolves the ordinary domain services on demand;
        // this is not a second execution path.
        if (simulationEnabled)
        {
            services.AddSingleton<SimWorkerRegistry>();
            services.AddHostedService<SimWorkerCommandWorker>();
        }

        // The host always owns one advisory lock. The bridge worker itself reports status through
        // EngineStatusReporter; the broad watchdog is unnecessary when autonomous workers are absent.
        services.AddHostedService<AdvisoryLockWatcherService>();
        if (!commandBridgeOnly)
        {
            services.AddHostedService<WorkerWatchdogService>();
        }

        return services;
    }
}
