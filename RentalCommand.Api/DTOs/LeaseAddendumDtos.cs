using System.Text.Json;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class LeaseAddendumSignerRequest
{
    public int? LeaseManagementPartyId { get; set; }
    public int? TenantId { get; set; }
    public LeaseLegalSignerRole? SignerRole { get; set; }
    public string NameSnapshot { get; set; } = string.Empty;
    public string EmailSnapshot { get; set; } = string.Empty;
    public short SigningOrder { get; set; }
    public bool IsRequired { get; set; } = true;
}

public sealed class LeaseAddendumFinancialEffectRequest
{
    public LeaseAddendumFinancialEffectType? EffectType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string ChargeCode { get; set; } = string.Empty;
    public DateOnly? EffectiveFromOn { get; set; }
    public DateOnly? EffectiveThroughOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class LeaseAddendumDraftSignerResponse
{
    public int LeaseAddendumSignerId { get; init; }
    public int? LeaseManagementPartyId { get; init; }
    public int? TenantId { get; init; }
    public LeaseLegalSignerRole SignerRole { get; init; }
    public string NameSnapshot { get; init; } = string.Empty;
    public string EmailSnapshot { get; init; } = string.Empty;
    public short SigningOrder { get; init; }
    public bool IsRequired { get; init; }
}

public sealed class LeaseAddendumDraftFinancialEffectResponse
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

public sealed class LeaseAddendumEligibleBaseAgreementPageResponse
{
    public IReadOnlyList<LeaseAddendumEligibleBaseAgreementResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeaseAddendumEligibleBaseAgreementResponse
{
    public int LeaseAgreementId { get; init; }
    public Guid PublicId { get; init; }
    public string AgreementNumber { get; init; } = string.Empty;
    public int VersionNumber { get; init; }
    public DateOnly TermStartOn { get; init; }
    public DateOnly? TermEndOn { get; init; }
    public DateOnly GoverningFromOn { get; init; }
    public string Currency { get; init; } = string.Empty;
}

public sealed class LeaseAddendumSignerCandidatesResponse
{
    public IReadOnlyList<LeaseManagementPartyResponse> Items { get; init; } = [];
}

/// <summary>
/// Exact, reloadable state for an unissued Addendum draft. DraftRevision is the optimistic
/// concurrency token required by both edit and issuance-preparation commands.
/// </summary>
public sealed class LeaseAddendumDraftDetailResponse
{
    public int LeaseManagementId { get; init; }
    public int LeaseAddendumId { get; init; }
    public Guid PublicId { get; init; }
    public Guid SeriesPublicId { get; init; }
    public int BaseAgreementId { get; init; }
    public Guid BaseAgreementPublicId { get; init; }
    public string BaseAgreementNumber { get; init; } = string.Empty;
    public string BaseAgreementCurrency { get; init; } = string.Empty;
    public int VersionNumber { get; init; }
    public int DraftRevision { get; init; }
    public string AddendumNumber { get; init; } = string.Empty;
    public LeaseAddendumPurpose Purpose { get; init; }
    public int? SourceAddendumId { get; init; }
    public DateOnly EffectiveFromOn { get; init; }
    public DateOnly? EffectiveThroughOn { get; init; }
    public int TermsSchemaVersion { get; init; }
    public JsonElement TermsPayload { get; init; }
    public int DocumentSourceVersionId { get; init; }
    public int? DocumentTemplateId { get; init; }
    public int? DocumentTemplateVersion { get; init; }
    public IReadOnlyList<LeaseAddendumDraftSignerResponse> Signers { get; init; } = [];
    public IReadOnlyList<LeaseAddendumDraftFinancialEffectResponse> FinancialEffects { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public class CreateLeaseAddendumDraftRequest
{
    public int BaseAgreementId { get; set; }
    public string AddendumNumber { get; set; } = string.Empty;
    public LeaseAddendumPurpose? Purpose { get; set; }
    public DateOnly EffectiveFromOn { get; set; }
    public DateOnly? EffectiveThroughOn { get; set; }
    public int TermsSchemaVersion { get; set; }
    public JsonElement TermsPayload { get; set; }
    public int DocumentTemplateId { get; set; }
    public List<LeaseAddendumSignerRequest> Signers { get; set; } = [];
    public List<LeaseAddendumFinancialEffectRequest> FinancialEffects { get; set; } = [];
}

public sealed class EditLeaseAddendumDraftRequest : CreateLeaseAddendumDraftRequest
{
    public int DraftRevision { get; set; }
}

public sealed class CorrectLeaseAddendumRequest
{
    public DateOnly SupersessionEffectiveOn { get; set; }
}

public sealed class IssueLeaseAddendumRequest
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

public sealed class VoidLegalArtifactRequest
{
    public string VoidReasonCode { get; set; } = string.Empty;
    public string? VoidNote { get; set; }
}

public sealed class CloseTenantAccountRequest
{
    public int TenantAccountId { get; set; }
    public string CloseReasonCode { get; set; } = string.Empty;
    public string? CloseNote { get; set; }
}

public sealed record LeaseAddendumDraftMutationResponse(
    int LeaseManagementId, int LeaseAddendumId, Guid SeriesPublicId, int VersionNumber,
    int DraftRevision, int? SourceAddendumId, IReadOnlyList<int> LeaseAddendumSignerIds,
    IReadOnlyList<int> FinancialEffectIds, bool Replayed)
{
    public static LeaseAddendumDraftMutationResponse From(LeaseAddendumDraftMutationResult result, bool replayed) =>
        new(result.LeaseManagementId, result.LeaseAddendumId, result.SeriesPublicId,
            result.VersionNumber, result.DraftRevision, result.SourceAddendumId,
            result.LeaseAddendumSignerIds, result.FinancialEffectIds, replayed);
}

public sealed record IssueLeaseAddendumResponse(Guid SignatureRequestPublicId,
    int LeaseManagementId, int LeaseAddendumId, int SignatureRequestId, int IssuedArtifactId, bool Replayed);
