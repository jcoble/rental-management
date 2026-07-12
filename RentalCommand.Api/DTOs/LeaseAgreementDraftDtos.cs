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
    public int DocumentTemplateVersion { get; set; }
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
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public DateOnly GoverningFromOn { get; set; }
    public List<LeaseRenewalAddendumDecisionRequest> AddendumDecisions { get; set; } = [];
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
    public string RequestFingerprint { get; set; } = string.Empty;
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
