using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Operations;

public sealed record RecoverVendorDispatchChronologyCommand(
    int PortfolioId,
    int WorkOrderId,
    int DispatchId,
    DateTime ExpectedContaminatedDispatchedAtUtc,
    int ExpectedStatusEventId,
    long ExpectedOutboxId,
    string ExpectedOutboxIdempotencyKey,
    string OriginalCommandIdempotencyKey,
    DateTime CorrectDispatchedAtUtc,
    int ActorUserId,
    DispatchManagementAccess ManagementAccess,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record RecoverVendorDispatchChronologyResult(
    int WorkOrderId,
    int DispatchId,
    int StatusEventId,
    long OutboxId,
    DateTime DispatchedAtUtc,
    bool WorkOrderUpdatedAtRepaired);
