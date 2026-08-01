using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Leasing;

public sealed record GivePossessionCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    int CreatedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string? Error);

public sealed record ReconcileHistoricalPossessionCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    DateOnly PossessionGivenOn,
    int CreatedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum ReconcileHistoricalPossessionOutcome
{
    Reconciled,
    AlreadyReconciled,
    RelationshipNotEligible,
    AgreementNotExecuted,
    AccountNotOpen,
    DateOutsideAgreementTerm,
    DateAfterBusinessDate,
    UnitUnavailable,
}

public sealed record ReconcileHistoricalPossessionResult(
    ReconcileHistoricalPossessionOutcome Outcome,
    int LeaseManagementId,
    int UnitId,
    DateTime? PossessionGivenAtUtc,
    string? Error);

/// <summary>
/// Confirms the complete physical move-in as one receipt-backed command. The handler derives the
/// governing deposit obligation and tenant/deposit accounts from the relationship inside the
/// transaction; callers cannot choose a different account or amount.
/// </summary>
public sealed record ConfirmMoveInCommand(
    int PortfolioId,
    int LeaseManagementId,
    int UnitId,
    DateOnly? DepositEffectiveOn,
    string? DepositPaymentMethodSummary,
    string? DepositExternalReference,
    int? MoveInAppointmentId,
    int CreatedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum ConfirmMoveInOutcome
{
    Confirmed,
    AlreadyConfirmed,
    RelationshipNotEligible,
    AgreementNotExecuted,
    AccountNotOpen,
    UnitUnavailable,
    DepositNotConfigured,
    DepositConflict,
    AppointmentInvalid,
}

public sealed record ConfirmMoveInResult(
    ConfirmMoveInOutcome Outcome,
    int LeaseManagementId,
    int UnitId,
    DateTime? PossessionGivenAtUtc,
    long? SecurityDepositEntryId,
    long? TenantLedgerEntryId,
    int? CompletedAppointmentId,
    string? Error);

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
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    IReadOnlyList<ReturnPossessionParty> Parties,
    IReadOnlyList<ReturnPossessionAccess> Accesses,
    string TurnoverReason,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string? Error);

public sealed record CompleteTurnoverCommand(
    int PortfolioId,
    int UnitId,
    int TurnoverPeriodId,
    int CreatedByUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] DateTime BusinessNowUtc,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

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
    string? Error);
