using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class TenantLedgerEntryListQuery : ListQuery
{
    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "direction")]
    public TenantLedgerDirection? Direction { get; set; }
}

public sealed class TenantChargeListQuery : ListQuery
{
    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "isPastDue")]
    public bool? IsPastDue { get; set; }
}

public sealed class TenantAccountListQuery : ListQuery
{
    [FromQuery(Name = "closed")]
    public bool? Closed { get; set; }

    [FromQuery(Name = "lifecycle")]
    public string? Lifecycle { get; set; }
}

public sealed class TenantAccountDepositListQuery : ListQuery
{
    [FromQuery(Name = "tenantAccountId")]
    [Range(1, int.MaxValue)]
    public int? TenantAccountId { get; set; }

    [FromQuery(Name = "propertyId")]
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "status")]
    [MaxLength(30)]
    public string? Status { get; set; }
}

public sealed class TenantLedgerEntryGlobalListQuery : ListQuery
{
    [FromQuery(Name = "propertyId")]
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [FromQuery(Name = "unitId")]
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [FromQuery(Name = "tenantAccountId")]
    [Range(1, int.MaxValue)]
    public int? TenantAccountId { get; set; }

    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "direction")]
    public TenantLedgerDirection? Direction { get; set; }
}

public sealed class TenantAccountPageResponse
{
    public IReadOnlyList<TenantAccountListItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class TenantAccountDepositPageResponse
{
    public IReadOnlyList<TenantAccountDepositListItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

/// <summary>One staff-authorized deposit account with its canonical database-derived position.</summary>
public sealed class TenantAccountDepositListItemResponse
{
    public int SecurityDepositAccountId { get; init; }
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int OriginatingAgreementId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? PrimaryTenantName { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime EffectiveNowUtc { get; init; }
    public DateOnly BusinessDate { get; init; }
    public decimal TotalReceived { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal TotalRefunded { get; init; }
    public decimal TotalTransferredIn { get; init; }
    public decimal TotalTransferredOut { get; init; }
    public decimal NetAdjustments { get; init; }
    public decimal HeldBalance { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>One staff-authorized canonical account suitable for account selection and review.</summary>
public sealed class TenantAccountListItemResponse
{
    public int TenantAccountId { get; init; }
    public Guid TenantAccountPublicId { get; init; }
    public int LeaseManagementId { get; init; }
    public Guid LeaseManagementPublicId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? PrimaryTenantName { get; init; }
    public string Lifecycle { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public DateTime OpenedAtUtc { get; init; }
    public DateTime? ClosedAtUtc { get; init; }
    public decimal ReceivableBalance { get; init; }
    public decimal PastDueAmount { get; init; }
    public int PastDueCount { get; init; }
}

public sealed class TenantLedgerEntryGlobalPageResponse
{
    public IReadOnlyList<TenantLedgerEntryGlobalResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

/// <summary>One authorized entry carrying enough account context for a cross-account staff queue.</summary>
public sealed class TenantLedgerEntryGlobalResponse
{
    public int FilteredTotalCount { get; init; }
    public decimal MonthCharges { get; init; }
    public decimal MonthPaymentsAndCredits { get; init; }
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? PrimaryTenantName { get; init; }
    public long TenantLedgerEntryId { get; init; }
    public Guid PublicId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public TenantLedgerDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public string BusinessKey { get; init; } = string.Empty;
    public Guid? TransferPublicId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? ReversesEntryId { get; init; }
    public long? ProviderPaymentAttemptId { get; init; }
    public int? SourceStoredFileId { get; init; }
    public int CreatedByUserId { get; init; }
}

public sealed class TenantAccountDetailResponse
{
    public int TenantAccountId { get; init; }
    public Guid TenantAccountPublicId { get; init; }
    public int LeaseManagementId { get; init; }
    public Guid LeaseManagementPublicId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public DateTime OpenedAtUtc { get; init; }
    public DateTime? ClosedAtUtc { get; init; }
    public string? CloseReasonCode { get; init; }
    public string? CloseNote { get; init; }
    public DateTime EffectiveNowUtc { get; init; }
    public DateOnly BusinessDate { get; init; }
    public decimal TotalDebits { get; init; }
    public decimal TotalCredits { get; init; }
    public decimal ReceivableBalance { get; init; }
    public decimal UnappliedCredit { get; init; }
    public decimal PastDueAmount { get; init; }
    public int PastDueCount { get; init; }
    public DateOnly? NextDueOn { get; init; }
    public decimal NextDueAmount { get; init; }
    public string Condition { get; init; } = string.Empty;
    public DateOnly? LastReceiptOn { get; init; }
    public decimal? LastReceiptAmount { get; init; }
}

public sealed class TenantLedgerEntryPageResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public IReadOnlyList<TenantLedgerEntryResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class TenantLedgerEntryResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public long TenantLedgerEntryId { get; init; }
    public Guid PublicId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public TenantLedgerDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public string BusinessKey { get; init; } = string.Empty;
    public Guid? TransferPublicId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? ReversesEntryId { get; init; }
    public bool HasReversal { get; init; }
    public long? ProviderPaymentAttemptId { get; init; }
    public int? SourceStoredFileId { get; init; }
    public int CreatedByUserId { get; init; }
}

/// <summary>
/// One exact immutable tenant ledger entry with its legal, provider, and source-file provenance.
/// Provider payloads, claim tokens, and server-side file paths are intentionally not exposed.
/// </summary>
public sealed class TenantLedgerEntryDetailResponse
{
    public int PortfolioId { get; init; }
    public int TenantAccountId { get; init; }
    public Guid TenantAccountPublicId { get; init; }
    public int LeaseManagementId { get; init; }
    public Guid LeaseManagementPublicId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? TenantName { get; init; }
    public long TenantLedgerEntryId { get; init; }
    public Guid PublicId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public TenantLedgerDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public string BusinessKey { get; init; } = string.Empty;
    public Guid? TransferPublicId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? ReversesEntryId { get; init; }
    public long? ProviderPaymentAttemptId { get; init; }
    public int? SourceStoredFileId { get; init; }
    public int CreatedByUserId { get; init; }
    public TenantLedgerAgreementProvenanceResponse? Agreement { get; init; }
    public TenantLedgerAddendumProvenanceResponse? Addendum { get; init; }
    public TenantLedgerProviderAttemptResponse? ProviderAttempt { get; init; }
    public TenantLedgerSourceFileResponse? SourceFile { get; init; }
}

public sealed class TenantLedgerAgreementProvenanceResponse
{
    public int LeaseAgreementId { get; init; }
    public Guid PublicId { get; init; }
    public int LeaseManagementId { get; init; }
    public int VersionNumber { get; init; }
    public string AgreementNumber { get; init; } = string.Empty;
    public LeaseAgreementChangeType ChangeType { get; init; }
    public int? TransferredFromAgreementId { get; init; }
    public int? ReplacesAgreementId { get; init; }
    public int? RenewsAgreementId { get; init; }
    public DateOnly GoverningFromOn { get; init; }
    public DateOnly? SupersededEffectiveOn { get; init; }
    public int? SupersededByAgreementId { get; init; }
    public DateTime? FullyExecutedAtUtc { get; init; }
    public DateTime? VoidedAtUtc { get; init; }
}

public sealed class TenantLedgerAddendumProvenanceResponse
{
    public int LeaseAddendumId { get; init; }
    public Guid PublicId { get; init; }
    public Guid SeriesPublicId { get; init; }
    public int LeaseManagementId { get; init; }
    public int BaseAgreementId { get; init; }
    public int VersionNumber { get; init; }
    public string AddendumNumber { get; init; } = string.Empty;
    public LeaseAddendumPurpose Purpose { get; init; }
    public int? ReplacesAddendumId { get; init; }
    public DateOnly EffectiveFromOn { get; init; }
    public DateOnly? EffectiveThroughOn { get; init; }
    public DateOnly? SupersededEffectiveOn { get; init; }
    public int? SupersededByAddendumId { get; init; }
    public DateTime? FullyExecutedAtUtc { get; init; }
    public DateTime? VoidedAtUtc { get; init; }
}

public sealed class TenantLedgerProviderAttemptResponse
{
    public long ProviderPaymentAttemptId { get; init; }
    public Guid PublicId { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string? ProviderReference { get; init; }
    public TenantPaymentAttemptType AttemptType { get; init; }
    public TenantPaymentAttemptState State { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? PaymentMethodSummary { get; init; }
    public string? PayerName { get; init; }
    public string? CheckNumber { get; init; }
    public string? BankName { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public DateTime PreparedAtUtc { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? SettledAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public int AttemptCount { get; init; }
    public DateTime? NextAttemptAtUtc { get; init; }
}

public sealed class TenantLedgerSourceFileResponse
{
    public int SourceStoredFileId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public DateTime UploadedAtUtc { get; init; }
    public DateTime? DeletedAtUtc { get; init; }
}

public sealed class TenantChargePageResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public IReadOnlyList<TenantChargeResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class TenantChargeResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public long TenantLedgerEntryId { get; init; }
    public Guid PublicId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public TenantLedgerDirection Direction { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal OriginalAmount { get; init; }
    public decimal ReversedAmount { get; init; }
    public decimal NetAllocations { get; init; }
    public decimal OpenAmount { get; init; }
    public bool IsPastDue { get; init; }
    public Guid? TransferPublicId { get; init; }
    public int? LeaseAgreementId { get; init; }
    public int? LeaseAddendumId { get; init; }
    public long? ReversesEntryId { get; init; }
    public long? ProviderPaymentAttemptId { get; init; }
    public int? SourceStoredFileId { get; init; }
}

public sealed class TenantAccountDepositResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int SecurityDepositAccountId { get; init; }
    public int OriginatingAgreementId { get; init; }
    public int PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public int UnitId { get; init; }
    public string UnitNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? PrimaryTenantName { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime EffectiveNowUtc { get; init; }
    public DateOnly BusinessDate { get; init; }
    public decimal TotalReceived { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal TotalRefunded { get; init; }
    public decimal TotalTransferredIn { get; init; }
    public decimal TotalTransferredOut { get; init; }
    public decimal NetAdjustments { get; init; }
    public decimal HeldBalance { get; init; }
    public string Status { get; init; } = string.Empty;
}
