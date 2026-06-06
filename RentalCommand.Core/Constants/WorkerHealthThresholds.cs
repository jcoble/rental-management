namespace RentalCommand.Core.Constants;

/// <summary>
/// Per-worker health thresholds (seconds) used by the watchdog and health check.
/// Healthy/Degraded/Down thresholds determine how late a heartbeat must be before
/// the status escalates. WatchdogStuckSeconds is the restart trigger threshold.
/// </summary>
public static class WorkerHealthThresholds
{
    public static readonly (int Healthy, int Degraded, int Down) DefaultThresholds = (60, 300, 600);

    /// <summary>
    /// Thresholds keyed by WorkerName (case-insensitive).
    /// Workers with frequent cycles get tighter bounds; slow workers get looser ones.
    /// </summary>
    public static readonly Dictionary<string, (int Healthy, int Degraded, int Down)> Thresholds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OutboxDispatchWorker"]          = (30,  120, 300),
            ["ScanProcessingWorker"]          = (60,  300, 600),
            ["RentChargeWorker"]              = (120, 600, 1800),
            ["LateFeeWorker"]                 = (120, 600, 1800),
            ["LeaseExpiryReminderWorker"]     = (120, 600, 1800),
            // Long-cycle workers (poll hourly / 6h / 12h / daily) only emit an idle keep-alive
            // heartbeat every ~2 min (EngineWorkerBase.HeartbeatInterval), so liveness — not work
            // cadence — drives these thresholds. Loose enough to ride out a missed keep-alive or
            // two before flagging a genuine hang.
            ["AutopayChargeWorker"]           = (180, 600, 1800),
            ["RecurringMaintenanceWorker"]    = (180, 600, 1800),
            ["NoticeDraftWorker"]             = (180, 600, 1800),
            ["DailyBriefingDeliveryWorker"]   = (180, 600, 1800),
        };

    /// <summary>
    /// How many seconds without a heartbeat before the watchdog considers a worker stuck.
    /// Should be comfortably larger than PollInterval + worst-case cycle time.
    /// </summary>
    public static readonly Dictionary<string, int> WatchdogStuckSeconds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OutboxDispatchWorker"]          = 120,
            ["ScanProcessingWorker"]          = 300,
            ["RentChargeWorker"]              = 600,
            ["LateFeeWorker"]                 = 600,
            ["LeaseExpiryReminderWorker"]     = 600,
            ["AutopayChargeWorker"]           = 600,
            ["RecurringMaintenanceWorker"]    = 600,
            ["NoticeDraftWorker"]             = 600,
            ["DailyBriefingDeliveryWorker"]   = 600,
        };

    public static (int Healthy, int Degraded, int Down) GetThresholds(string workerName)
    {
        if (string.IsNullOrWhiteSpace(workerName))
            return DefaultThresholds;

        return Thresholds.TryGetValue(workerName, out var t) ? t : DefaultThresholds;
    }

    public static bool IsKnownWorker(string? workerName) =>
        !string.IsNullOrWhiteSpace(workerName) && Thresholds.ContainsKey(workerName);
}
