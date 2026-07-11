using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Builds the bounded, database-side worker-health eligibility query used by the watchdog.
/// Keep the explicit worker predicates here: each worker has a different stale threshold, and
/// evaluating those predicates after materialization would make the database boundary unbounded.
/// </summary>
internal static class WorkerWatchdogEligibilityQuery
{
    internal static IQueryable<WorkerWatchdogObservation> Create(
        RentalCommandDbContext context,
        DateTime nowUtc)
    {
        var knownWorkerNames = WorkerHealthThresholds.WatchdogWorkerNames;
        var outboxCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["OutboxDispatchWorker"]);
        var scanCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["ScanProcessingWorker"]);
        var rentCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["RentChargeWorker"]);
        var lateFeeCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["LateFeeWorker"]);
        var leaseExpiryCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["LeaseExpiryReminderWorker"]);
        var autopayCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["AutopayChargeWorker"]);
        var maintenanceCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["RecurringMaintenanceWorker"]);
        var noticeCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["NoticeDraftWorker"]);
        var briefingCutoff = nowUtc.AddSeconds(-WorkerHealthThresholds.WatchdogStuckSeconds["DailyBriefingDeliveryWorker"]);

        return context.EngineWorkerHeartbeats
            .AsNoTracking()
            .Where(heartbeat => knownWorkerNames.Contains(heartbeat.WorkerName))
            .Select(heartbeat => new
            {
                WorkerName = heartbeat.WorkerName,
                LastHeartbeatUtc = heartbeat.LastHeartbeatUtc,
                IsErrored = heartbeat.Status == EngineWorkerStatus.Error,
                IsStalled =
                    (heartbeat.WorkerName == "OutboxDispatchWorker" && heartbeat.LastHeartbeatUtc < outboxCutoff) ||
                    (heartbeat.WorkerName == "ScanProcessingWorker" && heartbeat.LastHeartbeatUtc < scanCutoff) ||
                    (heartbeat.WorkerName == "RentChargeWorker" && heartbeat.LastHeartbeatUtc < rentCutoff) ||
                    (heartbeat.WorkerName == "LateFeeWorker" && heartbeat.LastHeartbeatUtc < lateFeeCutoff) ||
                    (heartbeat.WorkerName == "LeaseExpiryReminderWorker" && heartbeat.LastHeartbeatUtc < leaseExpiryCutoff) ||
                    (heartbeat.WorkerName == "AutopayChargeWorker" && heartbeat.LastHeartbeatUtc < autopayCutoff) ||
                    (heartbeat.WorkerName == "RecurringMaintenanceWorker" && heartbeat.LastHeartbeatUtc < maintenanceCutoff) ||
                    (heartbeat.WorkerName == "NoticeDraftWorker" && heartbeat.LastHeartbeatUtc < noticeCutoff) ||
                    (heartbeat.WorkerName == "DailyBriefingDeliveryWorker" && heartbeat.LastHeartbeatUtc < briefingCutoff),
            })
            .Select(observation => new WorkerWatchdogObservation
            {
                WorkerName = observation.WorkerName,
                LastHeartbeatUtc = observation.LastHeartbeatUtc,
                IsErrored = observation.IsErrored,
                IsStalled = observation.IsStalled,
                IsUnhealthy = observation.IsErrored || observation.IsStalled,
            })
            .OrderBy(observation => observation.WorkerName)
            .Take(knownWorkerNames.Count);
    }
}

internal sealed class WorkerWatchdogObservation
{
    public required string WorkerName { get; init; }
    public DateTime LastHeartbeatUtc { get; init; }
    public bool IsErrored { get; init; }
    public bool IsStalled { get; init; }
    public bool IsUnhealthy { get; init; }
}
