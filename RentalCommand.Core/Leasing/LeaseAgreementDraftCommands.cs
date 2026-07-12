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
    int DocumentTemplateVersion,
    IReadOnlyList<LeaseAgreementDraftSignerInput> Signers,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public sealed record LeaseRenewalAddendumDecisionInput(
    Guid SourceAddendumSeriesPublicId,
    LeaseRenewalAddendumDecisionType Decision,
    int? ReplacementAddendumId) : IAtomicCommandData;

public sealed record CreateLeaseAgreementSuccessorDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int SourceAgreementId,
    LeaseAgreementChangeType ChangeType,
    DateOnly TermStartOn,
    DateOnly? TermEndOn,
    DateOnly GoverningFromOn,
    IReadOnlyList<LeaseRenewalAddendumDecisionInput> AddendumDecisions,
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
}

public sealed record LeaseAgreementDraftMutationResult(
    LeaseAgreementDraftMutationOutcome Outcome,
    int LeaseManagementId,
    int LeaseAgreementId,
    int VersionNumber,
    int DraftRevision,
    int? SourceAgreementId,
    IReadOnlyList<int> LeaseAgreementSignerIds,
    IReadOnlyList<int> AddendumDecisionIds,
    string? Error) : IAtomicResultData;
