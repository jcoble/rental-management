using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>A chosen person and effective role in the relationship being prepared.</summary>
public sealed record PrepareMoveInParty(
    int TenantId,
    LeaseManagementPartyRole Role,
    bool GuarantorLegalNoticeEligible,
    string ChangeReason,
    bool IsAgreementSigner,
    short? SigningOrder,
    bool IsRequiredSigner) : IAtomicCommandData;

/// <summary>
/// Creates the complete pre-possession relationship root from one approved application. It does
/// not grant possession/access, post money, issue a legal artifact, or write legacy lease rows.
/// </summary>
public sealed record PrepareMoveInCommand(
    int PortfolioId,
    int ApplicationId,
    int UnitId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    DateTime? PlannedPossessionAtUtc,
    DateOnly PartyEffectiveFrom,
    IReadOnlyList<PrepareMoveInParty> Parties,
    int DocumentTemplateId,
    LeaseAgreementTermType TermType,
    DateOnly TermStartOn,
    DateOnly? TermEndOn,
    decimal BaseRentAmount,
    short RentDueDay,
    decimal SecurityDepositObligation,
    decimal LateFeeAmount,
    short GracePeriodDays,
    int TermsSchemaVersion,
    string TermsPayload,
    bool CreateSecurityDepositAccount,
    decimal? OpeningBalanceAmount,
    DateOnly? OpeningBalanceEffectiveOn,
    string? OpeningBalanceNote,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum PrepareMoveInOutcome
{
    Prepared,
    AlreadyPrepared,
    ApplicationNotApproved,
    UnitUnavailable,
    InvalidTemplate,
    InvalidParties,
    InvalidAgreementTerms,
}

/// <summary>Receipt-safe identifiers for every root created by Prepare move-in.</summary>
public sealed record PrepareMoveInResult(
    PrepareMoveInOutcome Outcome,
    int ApplicationId,
    int LeaseManagementId,
    int TenantAccountId,
    int LeaseAgreementId,
    long? OpeningBalanceLedgerEntryId,
    int? SecurityDepositAccountId,
    IReadOnlyList<int> LeaseManagementPartyIds,
    IReadOnlyList<int> LeaseAgreementSignerIds,
    string? Error) : IAtomicResultData;
