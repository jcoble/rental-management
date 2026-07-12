using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

public interface ILeasePartyAccessCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int LeaseManagementId { get; }
    int ActorUserId { get; }
    Guid AuthSessionId { get; }
    int AccessContextId { get; }
    long ExpectedAccessRevision { get; }
    string DeliveryIdempotencyKey { get; }
}

public sealed record AddEffectivePartyCommand(
    int PortfolioId,
    int LeaseManagementId,
    int TenantId,
    LeaseManagementPartyRole Role,
    DateOnly EffectiveFrom,
    bool GuarantorLegalNoticeEligible,
    string ChangeReason,
    bool SameRelationshipConfirmed,
    int? LegalBasisAgreementId,
    int? LegalBasisAddendumId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeasePartyAccessCommand;

public sealed record EndEffectivePartyCommand(
    int PortfolioId,
    int LeaseManagementId,
    int PartyId,
    DateOnly EffectiveThrough,
    TenantAccessDisposition AccessDisposition,
    int? PrimarySuccessorPartyId,
    bool SameRelationshipConfirmed,
    int? LegalBasisAgreementId,
    int? LegalBasisAddendumId,
    string ChangeReason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeasePartyAccessCommand;

public sealed record ChangeEffectivePartyRoleCommand(
    int PortfolioId,
    int LeaseManagementId,
    int PartyId,
    LeaseManagementPartyRole NewRole,
    DateOnly EffectiveOn,
    bool GuarantorLegalNoticeEligible,
    TenantAccessDisposition AccessDisposition,
    int? CompanionPrimaryPartyId,
    LeaseManagementPartyRole? CompanionNewRole,
    bool CompanionGuarantorLegalNoticeEligible,
    bool SameRelationshipConfirmed,
    int? LegalBasisAgreementId,
    int? LegalBasisAddendumId,
    string ChangeReason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeasePartyAccessCommand;

public sealed record GrantTenantUserAccessCommand(
    int PortfolioId,
    int LeaseManagementId,
    int PartyId,
    int ApplicationUserId,
    string Reason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeasePartyAccessCommand;

public sealed record RevokeTenantUserAccessCommand(
    int PortfolioId,
    int LeaseManagementId,
    int PartyId,
    int TenantUserAccessId,
    string Reason,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string DeliveryIdempotencyKey) : ILeasePartyAccessCommand;

public enum LeasePartyMutationOutcome
{
    Applied,
    InvalidEffectiveDate,
    InvalidParty,
    InvalidPrimaryTransition,
    LegalBasisRequired,
    AccessTransitionInvalid,
    AlreadyActive,
    AlreadyRevoked,
}

public sealed record LeasePartyMutationResult(
    LeasePartyMutationOutcome Outcome,
    int LeaseManagementId,
    int PartyId,
    int? ReplacementPartyId,
    int? CompanionReplacementPartyId,
    IReadOnlyList<int> TenantUserAccessIds,
    string? Error) : IAtomicResultData;
