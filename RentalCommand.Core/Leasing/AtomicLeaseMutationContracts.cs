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

/// <summary>
/// PostgreSQL-owned lease graph mutations. Input sets are validated, joined, partitioned, and
/// applied by one statement per operation; handlers never materialize a relationship graph.
/// </summary>
public interface IAtomicLeaseMutationPersistence
{
    Task<AtomicLegalDocumentSourceVersionResult> ResolveAuthoredDocumentSourceVersionAsync(
        int portfolioId,
        int propertyId,
        int leaseManagementId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<AtomicLegalDocumentSourceVersionResult> ResolveBuiltInDocumentSourceVersionAsync(
        int portfolioId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<AtomicLegalDocumentSourceVersionResult> ResolveImportedDocumentSourceVersionAsync(
        int portfolioId,
        int sourceStoredFileId,
        int sourceLegalDocumentArtifactId,
        string sourceContentSha256,
        string? sourceLabel,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<AtomicPropertyDispositionMutationResult?> CreatePropertyDispositionAsync(
        int portfolioId,
        int propertyId,
        DateTime closedOnDate,
        decimal salePrice,
        decimal sellingCosts,
        string? buyerName,
        string? memo,
        int actorUserId,
        DateTime changedAtUtc,
        DateOnly businessDate,
        CancellationToken ct = default);

    Task<bool> ValidateAgreementDraftSignerScopeAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicAgreementDraftSignerInput> signers,
        CancellationToken ct = default);

    Task<AtomicRenewalAddendumDraftResult> CreateRenewalAddendumDraftsAsync(
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int renewalAgreementId,
        DateOnly governingFromOn,
        IReadOnlyList<AtomicRenewalAddendumDecisionInput> decisions,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<AtomicLegalExecutionTransitionResult> ExecuteLegalArtifactTransitionAsync(
        int portfolioId,
        int leaseManagementId,
        int? leaseAgreementId,
        int? leaseAddendumId,
        int executedArtifactId,
        DateTime executedAtUtc,
        CancellationToken ct = default);

    Task<int> ReconcileInitialSecurityDepositChargeAsync(
        int portfolioId,
        int leaseAgreementId,
        DateTime postedAtUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<AtomicInitialSecurityDepositCharge>>
        ReconcileCompletedNativeEsignInitialSecurityDepositChargesAsync(
            int batchSize,
            CancellationToken ct = default);

    Task<IReadOnlyList<int>> CopyAgreementDraftSignersAsync(
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int successorAgreementId,
        CancellationToken ct = default);

    Task<IReadOnlyList<int>> CopyIssuedAgreementReplacementDraftSignersAsync(
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int replacementAgreementId,
        CancellationToken ct = default);

    Task<AtomicAddendumCorrectionChildCopyResult> CopyAddendumCorrectionChildrenAsync(
        int portfolioId,
        int leaseManagementId,
        int sourceAddendumId,
        int correctionAddendumId,
        CancellationToken ct = default);

    Task<AtomicAgreementDraftSignerReplacementResult> ReplaceAgreementDraftSignersAsync(
        int portfolioId,
        int leaseManagementId,
        int leaseAgreementId,
        int requiredDraftRevision,
        IReadOnlyList<AtomicAgreementDraftSignerInput> signers,
        CancellationToken ct = default);

    Task<AtomicTenantAccessTransitionResult> TransitionTenantAccessAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicTenantAccessTransition> transitions,
        int actorUserId,
        DateTime changedAtUtc,
        string reason,
        CancellationToken ct = default);

    Task<AtomicReturnPossessionMutationResult> ReturnPossessionAsync(
        int portfolioId,
        int leaseManagementId,
        int unitId,
        IReadOnlyList<AtomicReturnPossessionPartyInput> parties,
        IReadOnlyList<AtomicReturnPossessionAccessInput> accesses,
        int actorUserId,
        DateTime changedAtUtc,
        string turnoverReason,
        CancellationToken ct = default);

    Task<AtomicCancelPlannedRelationshipMutationResult> CancelPlannedRelationshipAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicCancelPlannedAccessInput> accesses,
        int actorUserId,
        DateTime changedAtUtc,
        string cancellationReasonCode,
        string? cancellationNote,
        string draftCancellationReason,
        CancellationToken ct = default);

    Task<AtomicTransferLeaseManagementMutationResult> TransferLeaseManagementAsync(
        TransferLeaseManagementCommand command,
        int destinationDocumentSourceVersionId,
        DateTime changedAtUtc,
        CancellationToken ct = default);
}
