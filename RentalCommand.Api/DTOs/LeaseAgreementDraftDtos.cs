using System.Text.Json;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class LeaseAgreementDraftSignerRequest
{
    public int? LeaseManagementPartyId { get; set; }
    public int? TenantId { get; set; }
    public LeaseLegalSignerRole? SignerRole { get; set; }
    public string NameSnapshot { get; set; } = string.Empty;
    public string EmailSnapshot { get; set; } = string.Empty;
    public short SigningOrder { get; set; }
    public bool IsRequired { get; set; } = true;
}

public sealed class LeaseAgreementDraftSignerResponse
{
    public int LeaseAgreementSignerId { get; init; }
    public int? LeaseManagementPartyId { get; init; }
    public int? TenantId { get; init; }
    public LeaseLegalSignerRole SignerRole { get; init; }
    public string NameSnapshot { get; init; } = string.Empty;
    public string EmailSnapshot { get; init; } = string.Empty;
    public short SigningOrder { get; init; }
    public bool IsRequired { get; init; }
}

/// <summary>
/// Exact, reloadable state for an unissued Agreement draft. DraftRevision is the optimistic
/// concurrency token required by both edit and issuance-preparation commands.
/// </summary>
public sealed class LeaseAgreementDraftDetailResponse
{
    public int LeaseManagementId { get; init; }
    public int LeaseAgreementId { get; init; }
    public Guid PublicId { get; init; }
    public int VersionNumber { get; init; }
    public int DraftRevision { get; init; }
    public string AgreementNumber { get; init; } = string.Empty;
    public LeaseAgreementChangeType ChangeType { get; init; }
    public string? CorrectionReason { get; init; }
    public LeaseAgreementTermType TermType { get; init; }
    public DateOnly TermStartOn { get; init; }
    public DateOnly? TermEndOn { get; init; }
    public DateOnly GoverningFromOn { get; init; }
    public decimal BaseRentAmount { get; init; }
    public short RentDueDay { get; init; }
    public decimal SecurityDepositObligation { get; init; }
    public decimal LateFeeAmount { get; init; }
    public short GracePeriodDays { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int TermsSchemaVersion { get; init; }
    public JsonElement TermsPayload { get; init; }
    public int DocumentSourceVersionId { get; init; }
    public int? DocumentTemplateId { get; init; }
    public int? DocumentTemplateVersion { get; init; }
    public IReadOnlyList<LeaseAgreementDraftSignerResponse> Signers { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class EditLeaseAgreementDraftRequest
{
    public int DraftRevision { get; set; }
    public string AgreementNumber { get; set; } = string.Empty;
    public LeaseAgreementTermType? TermType { get; set; }
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public DateOnly GoverningFromOn { get; set; }
    public decimal BaseRentAmount { get; set; }
    public short RentDueDay { get; set; }
    public decimal SecurityDepositObligation { get; set; }
    public decimal LateFeeAmount { get; set; }
    public short GracePeriodDays { get; set; }
    public int TermsSchemaVersion { get; set; }
    public JsonElement TermsPayload { get; set; }
    public int DocumentTemplateId { get; set; }
    public List<LeaseAgreementDraftSignerRequest> Signers { get; set; } = [];
}

public sealed class LeaseRenewalAddendumDecisionRequest
{
    public Guid SourceAddendumSeriesPublicId { get; set; }
    public LeaseRenewalAddendumDecisionType? Decision { get; set; }
}

public sealed class CreateLeaseAgreementSuccessorDraftRequest
{
    public LeaseAgreementChangeType? ChangeType { get; set; }
    public string? CorrectionReason { get; set; }
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public DateOnly GoverningFromOn { get; set; }
    public List<LeaseRenewalAddendumDecisionRequest> AddendumDecisions { get; set; } = [];
}

public sealed class CancelLeaseAgreementSuccessorDraftRequest
{
    public string CancellationReason { get; set; } = string.Empty;
}

public sealed record CancelLeaseAgreementSuccessorDraftResponse(
    int LeaseManagementId,
    int LeaseAgreementId,
    DateTime DraftCanceledAtUtc,
    int DraftCanceledByUserId,
    string DraftCancellationReason,
    bool Replayed);

public sealed class LeaseAgreementRenewalFinancialEffectSummaryResponse
{
    public int LeaseAddendumFinancialEffectId { get; init; }
    public LeaseAddendumFinancialEffectType EffectType { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string ChargeCode { get; init; } = string.Empty;
    public DateOnly? EffectiveFromOn { get; init; }
    public DateOnly? EffectiveThroughOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class LeaseAgreementEffectiveAddendumSeriesItemResponse
{
    public Guid SeriesPublicId { get; init; }
    public int CurrentLeaseAddendumId { get; init; }
    public Guid CurrentLeaseAddendumPublicId { get; init; }
    public int CurrentVersionNumber { get; init; }
    public int BaseAgreementId { get; init; }
    public string BaseAgreementNumber { get; init; } = string.Empty;
    public DateOnly BaseAgreementTermStartOn { get; init; }
    public DateOnly? BaseAgreementTermEndOn { get; init; }
    public LeaseAddendumPurpose Purpose { get; init; }
    /// <summary>The immutable legal Addendum number is the canonical display title.</summary>
    public string Title { get; init; } = string.Empty;
    public DateOnly EffectiveFromOn { get; init; }
    public DateOnly? EffectiveThroughOn { get; init; }
    public bool DecisionRequired { get; init; }
    public int FinancialEffectCount { get; init; }
    public IReadOnlyList<LeaseAgreementRenewalFinancialEffectSummaryResponse> FinancialEffects
        { get; init; } = [];
}

/// <summary>
/// The exact relationship-wide Addendum series set that a Renewal or Month-to-month successor
/// command must disposition. Source Agreement authority and the effective business date are DB-derived.
/// </summary>
public sealed class LeaseAgreementEffectiveAddendumSeriesResponse
{
    public int LeaseManagementId { get; init; }
    public int SourceAgreementId { get; init; }
    public string SourceAgreementNumber { get; init; } = string.Empty;
    public DateOnly SourceTermStartOn { get; init; }
    public DateOnly? SourceTermEndOn { get; init; }
    public DateOnly SourceGoverningFromOn { get; init; }
    public DateOnly BusinessDate { get; init; }
    public bool DecisionRequired { get; init; }
    public int RequiredDecisionCount { get; init; }
    public IReadOnlyList<LeaseAgreementEffectiveAddendumSeriesItemResponse> Series { get; init; } = [];
}

public sealed record LeaseAgreementDraftMutationResponse(
    int LeaseManagementId,
    int LeaseAgreementId,
    int VersionNumber,
    int DraftRevision,
    int? SourceAgreementId,
    IReadOnlyList<int> LeaseAgreementSignerIds,
    IReadOnlyList<int> AddendumDecisionIds,
    IReadOnlyList<int> ReplacementAddendumIds,
    bool Replayed)
{
    public static LeaseAgreementDraftMutationResponse FromResult(
        LeaseAgreementDraftMutationResult result,
        bool replayed) => new(
        result.LeaseManagementId,
        result.LeaseAgreementId,
        result.VersionNumber,
        result.DraftRevision,
        result.SourceAgreementId,
        result.LeaseAgreementSignerIds,
        result.AddendumDecisionIds,
        result.ReplacementAddendumIds,
        replayed);
}

public sealed class IssueLeaseAgreementRequest
{
    public int DraftRevision { get; set; }
    public Guid PendingUploadId { get; set; }
    public int DocumentSourceVersionId { get; set; }
    public string IssuanceFingerprint { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}

public sealed record IssueLeaseAgreementResponse(
    Guid SignatureRequestPublicId,
    int LeaseManagementId,
    int LeaseAgreementId,
    int SignatureRequestId,
    int IssuedArtifactId,
    bool Replayed);

/// <summary>Management-only signer progress; deliberately excludes every signing token and token digest.</summary>
public sealed class LeaseAgreementSignatureProgressSignerResponse
{
    public int SignatureSignerId { get; init; }
    public int LeaseAgreementSignerId { get; init; }
    public int? LeaseManagementPartyId { get; init; }
    public int? TenantId { get; init; }
    public LeaseLegalSignerRole SignerRole { get; init; }
    public string NameSnapshot { get; init; } = string.Empty;
    public string EmailSnapshot { get; init; } = string.Empty;
    public short SigningOrder { get; init; }
    public bool IsRequired { get; init; }
    public SignatureSignerStatus Status { get; init; }
    /// <summary>The invitation was durably queued at issuance; this is not provider delivery proof.</summary>
    public DateTime DeliveryQueuedAtUtc { get; init; }
    public DateTime? ViewedAtUtc { get; init; }
    public DateTime? ConsentGivenAtUtc { get; init; }
    public DateTime? SignedAtUtc { get; init; }
    public DateTime? DeclinedAtUtc { get; init; }
}

/// <summary>Authorized management view of one canonical Agreement signature packet.</summary>
public sealed class LeaseAgreementSignatureProgressResponse
{
    public int LeaseManagementId { get; init; }
    public int LeaseAgreementId { get; init; }
    public int SignatureRequestId { get; init; }
    public Guid SignatureRequestPublicId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public SignatureRequestStatus Status { get; init; }
    public int TotalSignerCount { get; init; }
    public int RequiredSignerCount { get; init; }
    public int SignedSignerCount { get; init; }
    public int DeclinedSignerCount { get; init; }
    public int IssuedArtifactId { get; init; }
    public bool IssuedArtifactReady { get; init; }
    public int? ExecutedArtifactId { get; init; }
    public bool ExecutedArtifactReady { get; init; }
    public DateTime PreparedAtUtc { get; init; }
    public DateTime? ProviderAcceptedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? DeclinedAtUtc { get; init; }
    public DateTime? VoidedAtUtc { get; init; }
    public string? FailureCode { get; init; }
    public IReadOnlyList<LeaseAgreementSignatureProgressSignerResponse> Signers { get; init; } = [];
}
