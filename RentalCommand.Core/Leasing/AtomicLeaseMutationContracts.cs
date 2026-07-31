namespace RentalCommand.Core.Leasing;

public enum AtomicTenantAccessTransitionKind
{
    Retain,
    Revoke,
    ContinueOnReplacement,
}

public sealed record AtomicTenantAccessTransition(
    int SourcePartyId,
    int? ReplacementPartyId,
    AtomicTenantAccessTransitionKind Kind);

public sealed record AtomicTenantAccessTransitionResult(
    IReadOnlyList<int> ActiveAccessIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> CreatedAccessIds);

public sealed record AtomicReturnPossessionPartyInput(
    int PartyId,
    ReturnPartyDisposition Disposition);

public sealed record AtomicReturnPossessionAccessInput(
    int AccessId,
    ReturnAccessDisposition Disposition);

public sealed record AtomicReturnPossessionMutationResult(
    ReturnPossessionOutcome Outcome,
    int? TurnoverPeriodId,
    DateTime? PossessionReturnedAtUtc,
    IReadOnlyList<int> EndedPartyIds,
    IReadOnlyList<int> RevokedAccessIds);

public sealed record AtomicCancelPlannedAccessInput(
    int AccessId,
    CancelPlannedAccessDisposition Disposition);

public sealed record AtomicCancelPlannedRelationshipMutationResult(
    CancelPlannedRelationshipOutcome Outcome,
    DateTime? CanceledAtUtc,
    int? TenantAccountId,
    IReadOnlyList<int> CanceledAgreementDraftIds,
    IReadOnlyList<int> CanceledAddendumDraftIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> RetainedAccessIds);

public sealed record AtomicTransferLeaseManagementMutationResult(
    TransferLeaseManagementOutcome Outcome,
    Guid TransferPublicId,
    int SourceTenantAccountId,
    int? SourceSecurityDepositAccountId,
    int DestinationLeaseManagementId,
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
    IReadOnlyList<long> SecurityDepositEntryIds);

public sealed class AtomicInitialSecurityDepositCharge
{
    public long LedgerEntryId { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseAgreementId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateOnly DueOn { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public DateTime PostedAtUtc { get; set; }
}

public sealed record AtomicAgreementDraftSignerInput(
    int? LeaseManagementPartyId,
    int? TenantId,
    int SignerRole,
    string NameSnapshot,
    string EmailSnapshot,
    short SigningOrder,
    bool IsRequired);

public sealed record AtomicAgreementDraftSignerReplacementResult(
    IReadOnlyList<int> DeletedSignerIds,
    IReadOnlyList<int> CreatedSignerIds);

public sealed record AtomicAddendumCorrectionChildCopyResult(
    bool Eligible,
    IReadOnlyList<int> CreatedSignerIds,
    IReadOnlyList<int> CreatedFinancialEffectIds);

public sealed record AtomicRenewalAddendumDecisionInput(
    Guid SourceAddendumSeriesPublicId,
    int Decision);

public sealed record AtomicRenewalAddendumDraftResult(
    bool InputValid,
    IReadOnlyList<int> DecisionIds,
    IReadOnlyList<int> ReplacementAddendumIds,
    IReadOnlyList<int> ReplacementSignerIds,
    IReadOnlyList<int> ReplacementFinancialEffectIds);

public enum AtomicLegalExecutionTransitionOutcome
{
    Applied,
    TargetChanged,
    SuccessorConflict,
    InvalidEffectiveDate,
    RenewalAddendumStateChanged,
}

public sealed record AtomicLegalExecutionTransitionResult(
    AtomicLegalExecutionTransitionOutcome Outcome,
    int? PredecessorId,
    IReadOnlyList<int> SupersededAddendumIds,
    IReadOnlyList<int> ReissuedAddendumIds);

public sealed record AtomicLegalDocumentSourceVersionResult(bool Resolved, int DocumentSourceVersionId);

public sealed record AtomicPropertyDispositionMutationResult(
    int DispositionId,
    IReadOnlyList<int> LeaseManagementIds,
    IReadOnlyList<int> TenantAccountIds,
    IReadOnlyList<int> AutopayEnrollmentIds,
    IReadOnlyList<int> PartyIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> AccessContextIds,
    IReadOnlyList<int> ManagementHoldIds,
    IReadOnlyList<int> UnitIds,
    IReadOnlyList<int> CapitalAssetIds);
