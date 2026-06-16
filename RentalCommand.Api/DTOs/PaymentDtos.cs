using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Payment"/>.</summary>
public class PaymentResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public PaymentType PaymentType { get; set; }
    public PaymentStatus Status { get; set; }
    public decimal Amount { get; set; }
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
        TenantName = e.Lease?.Tenant == null
            ? null
            : $"{e.Lease.Tenant.FirstName} {e.Lease.Tenant.LastName}".Trim(),
    };
}

public class CreatePaymentRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int LeaseId { get; set; }

    [EnumDataType(typeof(PaymentType))]
    public PaymentType PaymentType { get; set; } = PaymentType.Rent;

    [EnumDataType(typeof(PaymentStatus))]
    public PaymentStatus Status { get; set; } = PaymentStatus.Scheduled;

    [Range(0.01, 99999999)]
    public decimal Amount { get; set; }

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
