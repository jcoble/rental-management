using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Scanning;

public sealed record RejectScanDraftCommand(
    int PortfolioId,
    int DraftId,
    int UserId,
    DateTime ReviewedAtUtc,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    string? Reason) : IAtomicCommandData;

public sealed record RejectScanDraftResult(
    bool Rejected,
    int DraftId,
    DateTime? ReviewedAtUtc = null);
