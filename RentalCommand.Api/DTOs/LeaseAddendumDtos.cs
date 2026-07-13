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
