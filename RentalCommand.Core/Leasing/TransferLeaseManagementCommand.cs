using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Leasing;

/// <summary>
/// Moves one physically occupied household to another Unit without moving or rewriting its prior
/// legal and financial history. The destination Agreement is always a new editable draft.
/// </summary>
public sealed record TransferLeaseManagementCommand(
    int PortfolioId,
    int SourceLeaseManagementId,
    int SourceUnitId,
    int DestinationUnitId,
    int CreatedByUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    Guid TransferPublicId,
    DateOnly EffectiveOn,
    DateTime? PlannedDestinationPossessionAtUtc,
    bool GiveDestinationPossessionNow,
    string? PossessionAgreementExceptionReason,
    int DestinationDocumentTemplateId,
    bool CarryTenantBalance,
    bool CarrySecurityDeposit,
    string TransferReason,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public enum TransferLeaseManagementOutcome
{
    Transferred,
    AlreadyTransferred,
    SourcePossessionNotOpen,
    DestinationUnavailable,
    GoverningAgreementRequired,
    TenantAccountNotOpen,
    InvalidTemplate,
    InvalidHousehold,
    InvalidFinancialState,
}

public sealed record TransferLeaseManagementResult(
    TransferLeaseManagementOutcome Outcome,
    Guid TransferPublicId,
    int SourceLeaseManagementId,
    int SourceUnitId,
    int DestinationLeaseManagementId,
    int DestinationUnitId,
    int DestinationTenantAccountId,
    int DestinationAgreementId,
    int? DestinationSecurityDepositAccountId,
    int TurnoverPeriodId,
    DateTime? SourcePossessionReturnedAtUtc,
    DateTime? DestinationPossessionGivenAtUtc,
    decimal CarriedTenantBalance,
    decimal CarriedSecurityDeposit,
    IReadOnlyList<int> EndedSourcePartyIds,
    IReadOnlyList<int> DestinationPartyIds,
    IReadOnlyList<int> DestinationSignerIds,
    IReadOnlyList<int> RevokedSourceAccessIds,
    IReadOnlyList<int> DestinationAccessIds,
    IReadOnlyList<long> TenantLedgerEntryIds,
    IReadOnlyList<long> SecurityDepositEntryIds,
    string? Error) : IAtomicResultData;
