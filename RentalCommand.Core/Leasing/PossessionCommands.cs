using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Leasing;

public sealed record GivePossessionCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum GivePossessionOutcome
{
    Given,
    AlreadyGiven,
    RelationshipNotEligible,
    AgreementNotExecuted,
    AccountNotOpen,
    UnitUnavailable,
}

public sealed record GivePossessionResult(
    GivePossessionOutcome Outcome,
    int LeaseManagementId,
    int UnitId,
    DateTime? PossessionGivenAtUtc,
    string? Error) : IAtomicResultData;

public enum ReturnPartyDisposition
{
    EndMembership,
    RetainGuarantor,
}

public enum ReturnAccessDisposition
{
    RevokeNow,
    RetainHistorical,
}

public sealed record ReturnPossessionParty(
    int LeaseManagementPartyId,
    ReturnPartyDisposition Disposition) : IAtomicCommandData;

public sealed record ReturnPossessionAccess(
    int TenantUserAccessId,
    ReturnAccessDisposition Disposition) : IAtomicCommandData;

public sealed record ReturnPossessionCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    DateOnly EffectiveOn,
    IReadOnlyList<ReturnPossessionParty> Parties,
    IReadOnlyList<ReturnPossessionAccess> Accesses,
    string TurnoverReason,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum ReturnPossessionOutcome
{
    Returned,
    AlreadyReturned,
    PossessionNotGiven,
    InvalidPartyDisposition,
    InvalidAccessDisposition,
    TurnoverAlreadyOpen,
}

public sealed record ReturnPossessionResult(
    ReturnPossessionOutcome Outcome,
    int LeaseManagementId,
    int UnitId,
    int? TurnoverPeriodId,
    DateTime? PossessionReturnedAtUtc,
    string? Error) : IAtomicResultData;

public sealed record CompleteTurnoverCommand(
    int PortfolioId,
    int UnitId,
    int TurnoverPeriodId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum CompleteTurnoverOutcome
{
    Completed,
    AlreadyCompleted,
    TurnoverNotFound,
}

public sealed record CompleteTurnoverResult(
    CompleteTurnoverOutcome Outcome,
    int UnitId,
    int TurnoverPeriodId,
    DateTime? CompletedAtUtc,
    string? Error) : IAtomicResultData;
