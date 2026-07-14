using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>
/// Records the operational decision that drives lease-ending notice automation. This mutates the
/// continuous LeaseManagement episode only; it never rewrites an issued or executed Agreement.
/// </summary>
public sealed record RecordLeaseEndingDispositionCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    LeaseManagementEndingDisposition Disposition,
    DateTime? NoticeGivenAtUtc,
    DateTime? PlannedMoveOutAtUtc,
    string DecisionReason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public enum RecordLeaseEndingDispositionOutcome
{
    Recorded,
    RelationshipNotEligible,
    InvalidDates,
}

public sealed record RecordLeaseEndingDispositionResult(
    RecordLeaseEndingDispositionOutcome Outcome,
    int LeaseManagementId,
    LeaseManagementEndingDisposition EndingDisposition,
    DateTime? EndingDispositionDecidedAtUtc,
    int? EndingDispositionDecidedByUserId,
    DateTime? NoticeGivenAtUtc,
    DateTime? PlannedMoveOutAtUtc,
    string? Error) : IAtomicResultData;
