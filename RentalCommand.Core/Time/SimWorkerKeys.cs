namespace RentalCommand.Core.Time;

/// <summary>
/// Canonical worker-key vocabulary shared by the API enqueue endpoints (<c>/dev/workers</c>) and the
/// Engine's <c>SimWorkerRegistry</c>, so both agree on the exact key strings. Each individual key maps
/// 1:1 to an automation service; <see cref="RunDue"/> runs the due-order batch (<see cref="RunDueSequence"/>).
/// Lives in Core because the API cannot reference the Engine (dependency is <c>Engine → Api</c>).
/// </summary>
public static class SimWorkerKeys
{
    public const string RentCharge = "rent-charge";
    public const string NoticeDraft = "notice-draft";
    public const string TenantNoticeCandidates = "tenant-notice-candidates";
    public const string LateFee = "late-fee";
    public const string Autopay = "autopay";
    public const string DebtService = "debt-service";
    public const string RecurringExpense = "recurring-expense";
    public const string RecurringMaintenance = "recurring-maintenance";
    public const string DailyBriefing = "daily-briefing";
    public const string ScanProcessing = "scan-processing";

    /// <summary>Runs the full due-order batch (<see cref="RunDueSequence"/>) in one command.</summary>
    public const string RunDue = "run-due";

    /// <summary>All individual worker keys (excludes <see cref="RunDue"/>).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        RentCharge, TenantNoticeCandidates, NoticeDraft, LateFee, Autopay,
        DebtService, RecurringExpense, RecurringMaintenance, DailyBriefing, ScanProcessing,
    };

    /// <summary>
    /// run-due dependency order: rent-charge → tenant-notice candidates → notice-draft → late-fee →
    /// autopay → debt-service → recurring-expense → recurring-maintenance. daily-briefing is intentionally
    /// NOT part of run-due (E1 does not assert it).
    /// </summary>
    public static readonly IReadOnlyList<string> RunDueSequence = new[]
    {
        RentCharge, TenantNoticeCandidates, NoticeDraft, LateFee, Autopay,
        DebtService, RecurringExpense, RecurringMaintenance,
    };

    /// <summary>True if <paramref name="key"/> is a known individual key or <see cref="RunDue"/>.</summary>
    public static bool IsKnown(string key) => key == RunDue || All.Contains(key);
}
