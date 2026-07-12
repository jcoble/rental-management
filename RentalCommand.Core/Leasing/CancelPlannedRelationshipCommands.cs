using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Leasing;

public enum CancelPlannedAccessDisposition
{
    RevokeNow,
    Retain,
}

public sealed record CancelPlannedRelationshipAccess(
    int TenantUserAccessId,
    CancelPlannedAccessDisposition Disposition) : IAtomicCommandData;

public sealed record CancelPlannedRelationshipCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string CancellationReasonCode,
    string? CancellationNote,
    string DraftCancellationReason,
    IReadOnlyList<CancelPlannedRelationshipAccess> Accesses,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum CancelPlannedRelationshipOutcome
{
    Canceled,
    AlreadyCanceled,
    PossessionAlreadyGiven,
    IssuedArtifactsRequireResolution,
    FinancialResolutionRequired,
    InvalidAccessPolicy,
}

public sealed record CancelPlannedRelationshipResult(
    CancelPlannedRelationshipOutcome Outcome,
    int LeaseManagementId,
    int UnitId,
    DateTime? CanceledAtUtc,
    DateTime? AccountClosedAtUtc,
    IReadOnlyList<int> CanceledAgreementDraftIds,
    IReadOnlyList<int> CanceledAddendumDraftIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> RetainedAccessIds,
    string? Error) : IAtomicResultData;
