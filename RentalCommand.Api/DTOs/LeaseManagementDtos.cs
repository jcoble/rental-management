using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class LeaseManagementListQuery : ListQuery
{
    [FromQuery(Name = "propertyId")]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "unitId")]
    public int? UnitId { get; set; }

    [FromQuery(Name = "tenantId")]
    public int? TenantId { get; set; }

    [FromQuery(Name = "lifecycle")]
    public string? Lifecycle { get; set; }

    [FromQuery(Name = "hasReconciliationException")]
    public bool? HasReconciliationException { get; set; }
}

public sealed class LeaseManagementListResponse
{
    public IReadOnlyList<LeaseManagementSummaryResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeaseManagementSummaryResponse
{
    public int LeaseManagementId { get; init; }
    public Guid LeaseManagementPublicId { get; init; }
    public string RelationshipNumber { get; init; } = string.Empty;
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string Lifecycle { get; init; } = string.Empty;
    public DateOnly BusinessDate { get; init; }
    public int? LeaseAgreementId { get; init; }
    public string? AgreementNumber { get; init; }
    public string? AgreementStatus { get; init; }
    public DateOnly? TermStartOn { get; init; }
    public DateOnly? TermEndOn { get; init; }
    public decimal? BaseRentAmount { get; init; }
    public int? UpcomingLeaseAgreementId { get; init; }
    public int? TenantAccountId { get; init; }
    public int? PrimaryTenantId { get; init; }
    public string? PrimaryTenantName { get; init; }
    public int CurrentPartyCount { get; init; }
    public int CurrentResidentCount { get; init; }
    public int CurrentFinanciallyResponsiblePartyCount { get; init; }
    public bool HasReconciliationException { get; init; }
    public DateTime? PlannedPossessionAtUtc { get; init; }
    public DateTime? PossessionGivenAtUtc { get; init; }
    public DateTime? PlannedMoveOutAtUtc { get; init; }
    public DateTime? PossessionReturnedAtUtc { get; init; }
    public DateTime? AccountClosedAtUtc { get; init; }
    public DateTime? CanceledAtUtc { get; init; }
    public LeaseManagementEndingDisposition EndingDisposition { get; init; }
    public DateTime? NoticeGivenAtUtc { get; init; }
    public string? CancellationReasonCode { get; init; }
    public string? CancellationNote { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LeaseManagementDetailResponse
{
    public LeaseManagementSummaryResponse Summary { get; init; } = new();
    public LeaseManagementEndingDisposition EndingDisposition { get; init; }
    public DateTime? NoticeGivenAtUtc { get; init; }
    public string? CancellationReasonCode { get; init; }
    public string? CancellationNote { get; init; }
    public IReadOnlyList<LeaseManagementPartyResponse> Parties { get; init; } = [];
    public int AgreementCount { get; init; }
    public int AddendumCount { get; init; }
    public int LegalArtifactCount { get; init; }
}

public sealed class LeaseLegalHistoryQuery : ListQuery
{
    [FromQuery(Name = "status")]
    public string? Status { get; set; }
}

public sealed class LeaseAgreementHistoryPageResponse
{
    public IReadOnlyList<LeaseAgreementHistoryResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeaseAgreementHistoryResponse
{
    public int LeaseManagementId { get; init; }
    public int LeaseAgreementId { get; init; }
    public Guid PublicId { get; init; }
    public int VersionNumber { get; init; }
    public string AgreementNumber { get; init; } = string.Empty;
    public LeaseAgreementChangeType ChangeType { get; init; }
    public string? CorrectionReason { get; init; }
    public int? ReplacesAgreementId { get; init; }
    public int? RenewsAgreementId { get; init; }
    public LeaseAgreementTermType TermType { get; init; }
    public DateOnly TermStartOn { get; init; }
    public DateOnly? TermEndOn { get; init; }
    public DateOnly GoverningFromOn { get; init; }
    public DateOnly? SupersededEffectiveOn { get; init; }
    public decimal BaseRentAmount { get; init; }
    public string AgreementStatus { get; init; } = string.Empty;
    public bool IsGoverning { get; init; }
    public int SignerCount { get; init; }
    public bool HasSourceScan { get; init; }
    public LegalArtifactSummaryResponse? IssuedArtifact { get; init; }
    public LegalArtifactSummaryResponse? ExecutedArtifact { get; init; }
    public DateTime? IssuedAtUtc { get; init; }
    public DateTime? FullyExecutedAtUtc { get; init; }
    public DateTime? VoidedAtUtc { get; init; }
    public DateTime? DraftCanceledAtUtc { get; init; }
    public int? DraftCanceledByUserId { get; init; }
    public string? DraftCancellationReason { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LeaseAddendumHistoryPageResponse
{
    public IReadOnlyList<LeaseAddendumHistoryResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class LeaseAddendumHistoryResponse
{
    public int LeaseManagementId { get; init; }
    public int LeaseAddendumId { get; init; }
    public Guid PublicId { get; init; }
    public Guid SeriesPublicId { get; init; }
    public int BaseAgreementId { get; init; }
    public int VersionNumber { get; init; }
    public string AddendumNumber { get; init; } = string.Empty;
    public LeaseAddendumPurpose Purpose { get; init; }
    public int? ReplacesAddendumId { get; init; }
    public DateOnly EffectiveFromOn { get; init; }
    public DateOnly? EffectiveThroughOn { get; init; }
    public DateOnly? SupersededEffectiveOn { get; init; }
    public string AddendumStatus { get; init; } = string.Empty;
    public int FinancialEffectCount { get; init; }
    public decimal RecurringRentDelta { get; init; }
    public int SignerCount { get; init; }
    public bool CanCorrect { get; init; }
    public LegalArtifactSummaryResponse? IssuedArtifact { get; init; }
    public LegalArtifactSummaryResponse? ExecutedArtifact { get; init; }
    public DateTime? IssuedAtUtc { get; init; }
    public DateTime? FullyExecutedAtUtc { get; init; }
    public DateTime? VoidedAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed class LegalArtifactSummaryResponse
{
    public int LegalDocumentArtifactId { get; init; }
    public Guid PublicId { get; init; }
    public LegalDocumentArtifactKind ArtifactKind { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string ContentSha256 { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class LeaseManagementPartyResponse
{
    public int LeaseManagementPartyId { get; init; }
    public int LeaseManagementId { get; init; }
    public int TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public LeaseManagementPartyRole Role { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveThrough { get; init; }
    public bool IsCurrent { get; init; }
    public bool GuarantorLegalNoticeEligible { get; init; }
}

/// <summary>
/// An active portal-access grant that must receive an explicit disposition when a relationship
/// is canceled or possession is returned.
/// </summary>
public sealed class ActiveTenantUserAccessResponse
{
    public int TenantUserAccessId { get; init; }
    public Guid PublicId { get; init; }
    public int LeaseManagementPartyId { get; init; }
    public int AccessContextId { get; init; }
    public int ApplicationUserId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string UserDisplayName { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    public DateTime GrantedAtUtc { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Tenant-facing ledger for one continuous TenantAccount. Legal versions may change while this
/// account and its immutable entries continue beneath the same LeaseManagement relationship.
/// </summary>
public sealed class LeaseLedgerResponse
{
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string? PropertyName { get; set; }
    public decimal TotalCharged { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }
    public int PastDueCount { get; set; }
    public LedgerTransactionResponse? Opening { get; set; }
    public IReadOnlyList<LedgerTransactionResponse> Entries { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public string TestId => $"tenant-account-ledger-{TenantAccountId}";
}
