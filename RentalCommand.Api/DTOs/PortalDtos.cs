namespace RentalCommand.Api.DTOs;

using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

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
    public int? ExecutedStoredFileId { get; set; }
}

/// <summary>
/// Tenant-facing balance summary derived from the signed-in tenant's canonical account and ledger.
/// Outstanding is the open receivable; overdue is the subset whose due date has passed.
/// </summary>
public class PortalBalanceResponse
{
    public int TenantId { get; set; }

    /// <summary>Net immutable payment receipts already collected.</summary>
    public decimal Collected { get; set; }

    /// <summary>Sum of open receivables across the tenant's account relationships.</summary>
    public decimal Outstanding { get; set; }

    /// <summary>Sum of open charges that are past their due date.</summary>
    public decimal Overdue { get; set; }

    /// <summary>Count of open charges contributing to <see cref="Overdue"/>.</summary>
    public int OverdueCount { get; set; }

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => $"portal-balance-{TenantId}";
}

/// <summary>Tenant-facing projection of one immutable charge and its derived open balance.</summary>
public class PortalPaymentResponse
{
    /// <summary>The canonical debit <c>TenantLedgerEntry.Id</c>; never a legacy Payment id.</summary>
    public long Id { get; set; }
    public int TenantAccountId { get; set; }
    public int LeaseManagementId { get; set; }
    public string PaymentType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? Method { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>portal-payment-1</c>.</summary>
    public string TestId => $"portal-payment-{Id}";
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
}
