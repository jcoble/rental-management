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
    };
}

public class CreatePaymentRequest
{
    [Required]
    public int LeaseId { get; set; }

    public PaymentType PaymentType { get; set; } = PaymentType.Rent;
    public PaymentStatus Status { get; set; } = PaymentStatus.Scheduled;

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
}

public class UpdatePaymentRequest
{
    public PaymentType? PaymentType { get; set; }
    public PaymentStatus? Status { get; set; }
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
}
