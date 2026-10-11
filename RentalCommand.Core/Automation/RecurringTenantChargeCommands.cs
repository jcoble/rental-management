using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Automation;

/// <summary>
/// Materializes due recurring tenant-charge schedules. The schedule and occurrence business key
/// make separate engine runs converge on one tenant charge and one journal entry.
/// </summary>
public sealed record ApplyRecurringTenantChargeBatchCommand(
    Guid RunToken,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    int BatchSize) : IAtomicCommandData;

public sealed record ApplyRecurringTenantChargeBatchResult(
    int ChargeCount);
