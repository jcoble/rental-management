using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Scanning;

/// <summary>
/// Guided Setup's manual lease admission. The data handler turns this command into the same
/// Reviewing scan draft consumed by <see cref="ConfirmScanDraftCommand"/> so manual and scanned
/// leases share one canonical confirmation writer and transaction boundary.
/// </summary>
public sealed record CreateManualLeaseCommand(
    int PortfolioId,
    int UserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    ScanLeaseTargetData Target,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;
