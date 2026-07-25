using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Scanning;

public sealed record RejectScanDraftCommand(
    int PortfolioId,
    int DraftId,
    int UserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string? Reason) : IAtomicCommandData;

public sealed record RejectScanDraftResult(
    bool Rejected,
    int DraftId) : IAtomicResultData;
