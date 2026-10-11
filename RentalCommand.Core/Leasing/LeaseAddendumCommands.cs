using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

public interface ILeaseAddendumCommand : ILeaseAgreementDraftCommand
{
}

public sealed record LeaseAddendumDraftSignerInput(
    int? LeaseManagementPartyId,
    int? TenantId,
    LeaseLegalSignerRole SignerRole,
    string NameSnapshot,
    string EmailSnapshot,
    short SigningOrder,
    bool IsRequired) : IAtomicCommandData;

public sealed record LeaseAddendumFinancialEffectInput(
    LeaseAddendumFinancialEffectType EffectType,
    decimal Amount,
    string Currency,
    string ChargeCode,
    DateOnly? EffectiveFromOn,
    DateOnly? EffectiveThroughOn,
    DateOnly? DueOn,
    string Description) : IAtomicCommandData;

public sealed record CreateLeaseAddendumDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int BaseAgreementId,
    string AddendumNumber,
    LeaseAddendumPurpose Purpose,
    DateOnly EffectiveFromOn,
    DateOnly? EffectiveThroughOn,
    int TermsSchemaVersion,
    string TermsPayload,
    int DocumentTemplateId,
    IReadOnlyList<LeaseAddendumDraftSignerInput> Signers,
    IReadOnlyList<LeaseAddendumFinancialEffectInput> FinancialEffects,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAddendumCommand;

public sealed record EditLeaseAddendumDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAddendumId,
    int ExpectedDraftRevision,
    string AddendumNumber,
    LeaseAddendumPurpose Purpose,
    DateOnly EffectiveFromOn,
    DateOnly? EffectiveThroughOn,
    int TermsSchemaVersion,
    string TermsPayload,
    int DocumentTemplateId,
    IReadOnlyList<LeaseAddendumDraftSignerInput> Signers,
    IReadOnlyList<LeaseAddendumFinancialEffectInput> FinancialEffects,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAddendumCommand;

public sealed record CorrectLeaseAddendumDraftCommand(
    int PortfolioId,
    int LeaseManagementId,
    int SourceAddendumId,
    DateOnly SupersessionEffectiveOn,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAddendumCommand;

public enum LeaseAddendumDraftMutationOutcome
{
    Applied,
    StaleDraftRevision,
    DraftNotEditable,
    BaseAgreementNotEligible,
    SourceAddendumNotEligible,
    InvalidTerms,
    InvalidSigners,
    InvalidFinancialEffects,
}

public sealed record LeaseAddendumDraftMutationResult(
    LeaseAddendumDraftMutationOutcome Outcome,
    int LeaseManagementId,
    int LeaseAddendumId,
    Guid SeriesPublicId,
    int VersionNumber,
    int DraftRevision,
    int? SourceAddendumId,
    IReadOnlyList<int> LeaseAddendumSignerIds,
    IReadOnlyList<int> FinancialEffectIds,
    string? Error);

public sealed record VoidLeaseAgreementCommand(
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAgreementId,
    string VoidReasonCode,
    string? VoidNote,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public sealed record VoidLeaseAddendumCommand(
    int PortfolioId,
    int LeaseManagementId,
    int LeaseAddendumId,
    string VoidReasonCode,
    string? VoidNote,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAddendumCommand;

public enum VoidLegalArtifactOutcome
{
    Voided,
    AlreadyVoided,
    NotIssued,
    ActivePossessionDependsOnAgreement,
    ActiveAddendaDependOnAgreement,
    PostedMoneyRequiresResolution,
}

public sealed record VoidLegalArtifactResult(
    VoidLegalArtifactOutcome Outcome,
    int LeaseManagementId,
    int? LeaseAgreementId,
    int? LeaseAddendumId,
    DateTime? VoidedAtUtc,
    string? Error);

public sealed record CloseTenantAccountCommand(
    int PortfolioId,
    int LeaseManagementId,
    int TenantAccountId,
    string CloseReasonCode,
    string? CloseNote,
    int ActorUserId,
    [property: AtomicFingerprintIgnore] Guid AuthSessionId,
    [property: AtomicFingerprintIgnore] int AccessContextId,
    [property: AtomicFingerprintIgnore] long ExpectedAccessRevision,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : ILeaseAgreementDraftCommand;

public enum CloseTenantAccountOutcome
{
    Closed,
    AlreadyClosed,
    PossessionNotReturned,
    NonzeroReceivableBalance,
    NonzeroDepositBalance,
    UnfinishedWorkflow,
}

public sealed record CloseTenantAccountResult(
    CloseTenantAccountOutcome Outcome,
    int LeaseManagementId,
    int TenantAccountId,
    DateTime? ClosedAtUtc,
    string? Error);
