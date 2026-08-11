namespace RentalCommand.Api.DTOs;

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Enums;

/// <summary>Opaque tenant portal shell state; exposes no tenant, lease, unit, or property ids.</summary>
public sealed class PortalAccessStateResponse
{
    public bool HasActiveTenantAccess { get; set; }
}

/// <summary>
/// One tenant-visible rental relationship. The relationship survives agreement corrections and
/// renewals; <see cref="Agreement"/> is the database-selected governing agreement, or the upcoming
/// agreement when the relationship has not started yet.
/// </summary>
public class PortalLeaseRelationshipResponse
{
    public int LeaseManagementId { get; set; }
    public Guid LeaseManagementPublicId { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int TenantId { get; set; }
    public int? TenantAccountId { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public string Lifecycle { get; set; } = string.Empty;
    public string PropertyName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public PortalLeaseAgreementResponse? Agreement { get; set; }
}

/// <summary>Tenant-safe immutable agreement terms selected from the canonical agreement graph.</summary>
public class PortalLeaseAgreementResponse
{
    public int LeaseAgreementId { get; set; }
    public int VersionNumber { get; set; }
    public string AgreementNumber { get; set; } = string.Empty;
    public string AgreementStatus { get; set; } = string.Empty;
    public bool IsGoverning { get; set; }
    public LeaseAgreementChangeType ChangeType { get; set; }
    public LeaseAgreementTermType TermType { get; set; }
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public decimal BaseRentAmount { get; set; }
    public decimal SecurityDepositObligation { get; set; }
    public decimal LateFeeAmount { get; set; }
    public short RentDueDay { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime? FullyExecutedAtUtc { get; set; }
    public bool ExecutedDocumentAvailable { get; set; }
    public string? ExecutedDocumentFileName { get; set; }
    public string? ExecutedDocumentContentType { get; set; }
}

public sealed class PortalTenantAccountListQuery : ListQuery
{
    [FromQuery(Name = "lifecycle")]
    public string? Lifecycle { get; set; }

    [FromQuery(Name = "closed")]
    public bool? Closed { get; set; }
}

public sealed class PortalTenantLedgerEntryListQuery : ListQuery
{
    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "direction")]
    public TenantLedgerDirection? Direction { get; set; }
}

public sealed class PortalTenantAccountHistoryQuery : ListQuery
{
    /// <summary>
    /// Database-resolved period: currentMonth, last3Months, thisYear, all, or custom.
    /// Custom uses the inclusive <see cref="ListQuery.From"/> and <see cref="ListQuery.To"/> days.
    /// </summary>
    [FromQuery(Name = "period")]
    public string Period { get; set; } = "currentMonth";

    /// <summary>
    /// An entry opened from a notification or deep link is returned with the requested page even
    /// when it falls outside that page. It must still belong to the selected period and account.
    /// </summary>
    [FromQuery(Name = "entry")]
    public long? FocusedEntryId { get; set; }

    internal string NormalizedPeriod => Period.Trim().ToLowerInvariant() switch
    {
        "currentmonth" => "currentMonth",
        "previousmonth" => "previousMonth",
        "last3months" => "last3Months",
        "thisyear" => "thisYear",
        "all" => "all",
        "custom" => "custom",
        _ => "currentMonth",
    };
}

public sealed class PortalTenantChargeListQuery : ListQuery
{
    [FromQuery(Name = "entryType")]
    public TenantLedgerEntryType? EntryType { get; set; }

    [FromQuery(Name = "isPastDue")]
    public bool? IsPastDue { get; set; }
}

public sealed class PortalTenantWorkOrderListQuery : ListQuery
{
    [FromQuery(Name = "status")]
    public WorkOrderStatus? Status { get; set; }

    [FromQuery(Name = "openOnly")]
    public bool? OpenOnly { get; set; }
}

public sealed class PortalTenantWorkOrderPageResponse
{
    public IReadOnlyList<WorkOrderResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class PortalTenantAccountPageResponse
{
    public IReadOnlyList<PortalTenantAccountResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

/// <summary>One canonical account the current portal access context may read.</summary>
public sealed class PortalTenantAccountResponse
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
    public string Lifecycle { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public DateTime OpenedAtUtc { get; init; }
    public DateTime? ClosedAtUtc { get; init; }
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
    public PortalLeaseAgreementResponse? CurrentAgreement { get; init; }
    public PortalTenantAccountDepositResponse? Deposit { get; init; }
}

public sealed class PortalTenantLedgerEntryPageResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public IReadOnlyList<PortalTenantLedgerEntryResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class PortalTenantLedgerEntryResponse
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
    public long? ProviderPaymentAttemptId { get; init; }
    public int? SourceStoredFileId { get; init; }
}

public sealed class PortalTenantAccountHistoryResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly BusinessDate { get; init; }
    public string Period { get; init; } = string.Empty;
    public DateOnly? PeriodFrom { get; init; }
    public DateOnly PeriodTo { get; init; }
    /// <summary>Positive means owed; negative means the tenant has a credit.</summary>
    public decimal CurrentDue { get; init; }
    public decimal BeginningBalance { get; init; }
    public decimal ClosingBalance { get; init; }
    public IReadOnlyList<PortalTenantAccountHistoryItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class PortalTenantAccountHistoryItemResponse
{
    public long TenantLedgerEntryId { get; init; }
    public TenantLedgerEntryType EntryType { get; init; }
    public TenantLedgerDirection Direction { get; init; }
    public string DisplayType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateOnly EffectiveOn { get; init; }
    public DateOnly? DueOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    /// <summary>Debit is positive and increases amount owed; credit is negative and reduces it.</summary>
    public decimal SignedAmount { get; init; }
    public decimal RunningBalance { get; init; }
    public decimal OpenAmount { get; init; }
    public bool Payable { get; init; }
    public long? ReversesEntryId { get; init; }
    public long? ReversedByEntryId { get; init; }
    public bool IsFocused { get; init; }
    public IReadOnlyList<PortalTenantAllocationRefResponse> Allocations { get; init; } = [];
}

public sealed class PortalTenantAllocationRefResponse
{
    public long AllocationId { get; init; }
    public long TargetSourceId { get; init; }
    public Guid TargetPublicId { get; init; }
    public string TargetDescription { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public DateOnly EffectiveOn { get; init; }
}

public sealed class PortalTenantChargePageResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public IReadOnlyList<PortalTenantChargeResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public sealed class PortalTenantChargeResponse
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

public sealed class PortalTenantAccountDepositResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int SecurityDepositAccountId { get; init; }
    public int OriginatingAgreementId { get; init; }
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

/// <summary>
/// Optional per-request override of where Stripe Checkout returns the tenant. When omitted the
/// server falls back to its configured (or built-in) success/cancel URLs.
/// </summary>
public class PortalCheckoutRequest
{
    [MaxLength(2048)]
    public string? SuccessUrl { get; set; }

    [MaxLength(2048)]
    public string? CancelUrl { get; set; }
}

/// <summary>Hosted-Checkout response: the URL the frontend redirects the tenant to.</summary>
public class CheckoutSessionResponse
{
    public string CheckoutUrl { get; set; } = string.Empty;
    public long? PaymentAttemptId { get; set; }
    public string? AttemptState { get; set; }
    public bool AlreadyPaid { get; set; }
}

/// <summary>Request to enroll the tenant's own canonical account in autopay.</summary>
public class AutopayEnrollRequest
{
    [Required, MaxLength(186)]
    public string OperationKey { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? SuccessUrl { get; set; }

    [MaxLength(2048)]
    public string? CancelUrl { get; set; }
}

/// <summary>The tenant's autopay enrollment status for a canonical tenant account.</summary>
public class AutopayStatusResponse
{
    public int TenantAccountId { get; set; }
    public bool Active { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public bool OnlinePaymentsAvailable { get; set; }
}

public class CreateTenantWorkOrderRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Category { get; set; } = "Resident Request";

    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;

    [MaxLength(64)]
    public string? ContactPhone { get; set; }

    [MaxLength(320)]
    public string? ContactEmail { get; set; }

    public bool? ResidentMustBePresent { get; set; }
    public bool? CallBeforeEntry { get; set; }
    public bool? CallIfNotHome { get; set; }
    public bool? PermissionToEnter { get; set; }

    [MaxLength(2000)]
    public string? EntryNotes { get; set; }

    [MaxLength(2000)]
    public string? PetWarnings { get; set; }

    [MaxLength(2000)]
    public string? AccessWarnings { get; set; }
}
