using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Payment"/>.</summary>
public class PaymentResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The lease this payment is on; null for a lease-less payment (e.g. an application fee).</summary>
    public int? LeaseId { get; set; }

    /// <summary>The application this payment is on, when it is a lease-less application/screening fee; null otherwise.</summary>
    public int? ApplicationId { get; set; }

    /// <summary>
    /// Unit the payment's lease is on; resolved DB-side via the lease join so the web client can route the
    /// payment to its unit's Command Center tab. Null only if the lease navigation wasn't loaded.
    /// </summary>
    public int? UnitId { get; set; }

    /// <summary>
    /// Property the payment's lease is on; resolved DB-side via the lease join so the web client can route the
    /// payment to its unit's Command Center tab. Null only if the lease navigation wasn't loaded.
    /// </summary>
    public int? PropertyId { get; set; }

    public PaymentType PaymentType { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// Cash collected so far when the payment is <see cref="PaymentStatus.Partial"/> (strictly between
    /// 0 and <see cref="Amount"/>); null otherwise. A Paid payment is fully collected (<see cref="Amount"/>).
    /// </summary>
    public decimal? AmountPaid { get; set; }

    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? Method { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Human-readable lease number for the payment's lease; populated when the Lease navigation is loaded.</summary>
    public string? LeaseNumber { get; set; }

    /// <summary>Tenant name on the payment's lease; populated when the Lease/Tenant navigations are loaded.</summary>
    public string? TenantName { get; set; }

    /// <summary>Property name on the payment's lease; populated when the Lease/Property navigation is loaded.</summary>
    public string? PropertyName { get; set; }

    /// <summary>Unit number on the payment's lease; populated when the Lease/Unit navigation is loaded.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Name on the check / of the payer; from a scanned rent check.</summary>
    public string? PayerName { get; set; }

    /// <summary>Check number; from a scanned rent check.</summary>
    public string? CheckNumber { get; set; }

    /// <summary>Issuing bank name; from a scanned rent check.</summary>
    public string? BankName { get; set; }

    /// <summary>True when a scanned source document is attached to this payment (drives the detail-page viewer).</summary>
    public bool HasScan { get; set; }

    /// <summary>True when the attached scan is an image (vs a PDF) — lets the UI show a thumbnail.</summary>
    public bool ScanIsImage { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>payment-1</c>.</summary>
    public string TestId => $"payment-{Id}";

    public static PaymentResponse FromEntity(Payment e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        LeaseId = e.LeaseId,
        PaymentType = e.PaymentType,
        Status = e.Status,
        Amount = e.Amount,
        AmountPaid = e.AmountPaid,
        DueDate = e.DueDate,
        PaidDate = e.PaidDate,
        Method = e.Method,
        ExternalReference = e.ExternalReference,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        PayerName = e.PayerName,
        CheckNumber = e.CheckNumber,
        BankName = e.BankName,
        // Lease label for the detail/list view. Only set when the Lease navigation was loaded
        // (Include'd); left null otherwise so callers that don't join don't pay for it.
        LeaseNumber = e.Lease?.LeaseNumber,
        ApplicationId = e.ApplicationId,
        UnitId = e.Lease?.UnitId,
        // Lease-tied payments scope by the lease's property; a lease-less fee carries its own PropertyId.
        PropertyId = e.Lease?.PropertyId ?? e.PropertyId,
        PropertyName = e.Lease?.Property?.Name,
        UnitNumber = e.Lease?.Unit?.UnitNumber,
        TenantName = e.Lease?.Tenant == null
            ? null
            : $"{e.Lease.Tenant.FirstName} {e.Lease.Tenant.LastName}".Trim(),
    };
}

public class PaymentListResponse
{
    public IReadOnlyList<PaymentReceiptResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>
/// One immutable receipt credit on a continuous TenantAccount. This is the canonical payment read
/// shape; contractual charges and their open balances are separate ledger entries/projections.
/// </summary>
public sealed class PaymentReceiptResponse
{
    public long Id { get; init; }
    public Guid PublicId { get; init; }
    public int PortfolioId { get; init; }
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? TenantName { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly ReceivedOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public string? ProviderReference { get; init; }
    public TenantPaymentAttemptState? ProviderState { get; init; }
    public string? PaymentMethodSummary { get; init; }
    public string? PayerName { get; init; }
    public string? CheckNumber { get; init; }
    public string? BankName { get; init; }
    public int? SourceStoredFileId { get; init; }
}

public class PaymentListQuery : ListQuery
{
    [FromQuery(Name = "tenantAccountId")]
    [Range(1, int.MaxValue)]
    public int? TenantAccountId { get; set; }

    [FromQuery(Name = "leaseManagementId")]
    [Range(1, int.MaxValue)]
    public int? LeaseManagementId { get; set; }

    [FromQuery(Name = "applicationId")]
    [Range(1, int.MaxValue)]
    public int? ApplicationId { get; set; }

    [FromQuery(Name = "dueFrom")]
    public DateTime? DueFrom { get; set; }

    [FromQuery(Name = "dueTo")]
    public DateTime? DueTo { get; set; }

    [FromQuery(Name = "paidFrom")]
    public DateTime? PaidFrom { get; set; }

    [FromQuery(Name = "paidTo")]
    public DateTime? PaidTo { get; set; }
}

public class CreatePaymentRequest
{
    /// <summary>
    /// The lease to charge. Optional: a lease-less payment (application/screening fee) sets
    /// <see cref="ApplicationId"/> instead. gap5 (G5.2) completes the "exactly one of LeaseId /
    /// ApplicationId" validation in <c>PaymentService.CreateAsync</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    /// <summary>
    /// The rental application to charge, for a lease-less application/screening fee. Exactly one of
    /// <see cref="LeaseId"/> / <see cref="ApplicationId"/> must be set (validated in the service). When set,
    /// <see cref="PaymentType"/> defaults to <see cref="PaymentType.ApplicationFee"/>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? ApplicationId { get; set; }

    /// <summary>
    /// The property to attribute the payment to when there is no lease (application fee). Optional; defaults
    /// to the application's property so lease-less income still lands on the right property's reports.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    [EnumDataType(typeof(PaymentType))]
    public PaymentType PaymentType { get; set; } = PaymentType.Rent;

    [EnumDataType(typeof(PaymentStatus))]
    public PaymentStatus Status { get; set; } = PaymentStatus.Scheduled;

    [Range(0.01, 99999999)]
    public decimal Amount { get; set; }

    /// <summary>
    /// For a <see cref="PaymentStatus.Partial"/> payment, the cash collected so far. Must be strictly
    /// between 0 and <see cref="Amount"/> (validated in the service). Ignored/cleared for other statuses.
    /// </summary>
    [Range(0, 99999999)]
    public decimal? AmountPaid { get; set; }

    [Required]
    public DateTime DueDate { get; set; }

    public DateTime? PaidDate { get; set; }

    [MaxLength(100)]
    public string? Method { get; set; }

    [MaxLength(200)]
    public string? ExternalReference { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Name on the check / of the payer; promoted from a scanned rent check.</summary>
    [MaxLength(200)]
    public string? PayerName { get; set; }

    /// <summary>Check number; promoted from a scanned rent check.</summary>
    [MaxLength(100)]
    public string? CheckNumber { get; set; }

    /// <summary>Issuing bank name; promoted from a scanned rent check.</summary>
    [MaxLength(200)]
    public string? BankName { get; set; }

    /// <summary>Full scan-extraction superset JSON (jsonb); populated when creating from a scan draft.</summary>
    public string? ExtractedData { get; set; }
}

public class UpdatePaymentRequest
{
    /// <summary>
    /// Reassign the payment to a different lease. Omitted (null) leaves the existing lease untouched;
    /// when supplied the lease is validated to be in the caller's portfolio before it is applied.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? LeaseId { get; set; }

    [EnumDataType(typeof(PaymentType))]
    public PaymentType? PaymentType { get; set; }

    [EnumDataType(typeof(PaymentStatus))]
    public PaymentStatus? Status { get; set; }

    [Range(0.01, 99999999)]
    public decimal? Amount { get; set; }

    /// <summary>
    /// For a <see cref="PaymentStatus.Partial"/> payment, the cash collected so far (strictly between 0
    /// and the payment's Amount; validated in the service). Set to 0 to clear it back to "nothing collected".
    /// </summary>
    [Range(0, 99999999)]
    public decimal? AmountPaid { get; set; }

    public DateTime? DueDate { get; set; }
    public DateTime? PaidDate { get; set; }

    [MaxLength(100)]
    public string? Method { get; set; }

    [MaxLength(200)]
    public string? ExternalReference { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Marks a scheduled payment as paid. Optional fields default the paid date to now and capture how the
/// money arrived for the record.
/// </summary>
public class MarkPaidRequest
{
    /// <summary>When the payment was received; defaults to UtcNow when omitted.</summary>
    public DateTime? PaidDate { get; set; }

    [MaxLength(100)]
    public string? Method { get; set; }

    [MaxLength(200)]
    public string? ExternalReference { get; set; }

    /// <summary>Optional free-text note captured when marking the payment paid (persisted to <see cref="Payment.Notes"/>).</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>Result of settling every still-owed past-due payment on one lease in one server action.</summary>
public class MarkLeasePastDuePaidResponse
{
    public int LeaseId { get; set; }
    public int MarkedPaidCount { get; set; }
    public IReadOnlyList<int> PaymentIds { get; set; } = [];
}
