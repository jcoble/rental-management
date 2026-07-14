using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

public interface ILeaseAgreementDraftCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int LeaseManagementId { get; }
    int ActorUserId { get; }
    Guid AuthSessionId { get; }
    int AccessContextId { get; }
    long ExpectedAccessRevision { get; }
    string DeliveryIdempotencyKey { get; }
}

public sealed record LeaseAgreementDraftSignerInput(
    int? LeaseManagementPartyId,
    int? TenantId,
    LeaseLegalSignerRole SignerRole,
    string NameSnapshot,
    string EmailSnapshot,
    short SigningOrder,
    bool IsRequired) : IAtomicCommandData;

public sealed record EditLeaseAgreementDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAgreementId,
    int ExpectedDraftRevision,
    string AgreementNumber,
    LeaseAgreementTermType TermType,
    DateOnly TermStartOn,
    DateOnly? TermEndOn,
    DateOnly GoverningFromOn,
    decimal BaseRentAmount,
    short RentDueDay,
    decimal SecurityDepositObligation,
    decimal LateFeeAmount,
    short GracePeriodDays,
    int TermsSchemaVersion,
    string TermsPayload,
    int DocumentTemplateId,
    IReadOnlyList<LeaseAgreementDraftSignerInput> Signers,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public sealed record LeaseRenewalAddendumDecisionInput(
    Guid SourceAddendumSeriesPublicId,
    LeaseRenewalAddendumDecisionType Decision) : IAtomicCommandData;

public sealed record CreateLeaseAgreementSuccessorDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int SourceAgreementId,
    LeaseAgreementChangeType ChangeType,
    DateOnly TermStartOn,
    DateOnly? TermEndOn,
    DateOnly GoverningFromOn,
    string? CorrectionReason,
    IReadOnlyList<LeaseRenewalAddendumDecisionInput> AddendumDecisions,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public sealed record ReplaceIssuedAgreementWithDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int SourceAgreementId,
    string? VoidNote,
    string ReissueReason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public enum LeaseAgreementDraftMutationOutcome
{
    Applied,
    StaleDraftRevision,
    DraftNotEditable,
    InvalidTerms,
    InvalidSigners,
    SourceAgreementNotCurrent,
    InvalidSuccessorType,
    InvalidAddendumDecisions,
    SourceAgreementNotRecoverable,
}

public sealed record CancelLeaseAgreementSuccessorDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAgreementId,
    string CancellationReason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public enum CancelLeaseAgreementSuccessorDraftOutcome
{
    Canceled,
    AlreadyCanceled,
    NotSuccessorDraft,
    IssuedOrExecuted,
}

public sealed record CancelLeaseAgreementSuccessorDraftResult(
    CancelLeaseAgreementSuccessorDraftOutcome Outcome,
    int LeaseManagementId,
    int LeaseAgreementId,
    DateTime? DraftCanceledAtUtc,
    int? DraftCanceledByUserId,
    string? DraftCancellationReason,
    string? Error) : IAtomicResultData;

public sealed record LeaseAgreementDraftMutationResult(
    LeaseAgreementDraftMutationOutcome Outcome,
    int LeaseManagementId,
    int LeaseAgreementId,
    int VersionNumber,
    int DraftRevision,
    int? SourceAgreementId,
    IReadOnlyList<int> LeaseAgreementSignerIds,
    IReadOnlyList<int> AddendumDecisionIds,
    IReadOnlyList<int> ReplacementAddendumIds,
    string? Error) : IAtomicResultData;
