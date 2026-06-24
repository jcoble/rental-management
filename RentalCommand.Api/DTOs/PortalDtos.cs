namespace RentalCommand.Api.DTOs;

using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

/// <summary>
/// Tenant-facing balance summary derived from the signed-in tenant's payment records. Outstanding is
/// everything still owed (scheduled/partial/late, not yet paid or waived); overdue is the subset whose
/// due date has passed.
/// </summary>
public class PortalBalanceResponse
{
    public int TenantId { get; set; }

    /// <summary>Sum of payments already collected (status Paid).</summary>
    public decimal Collected { get; set; }

    /// <summary>Sum of payments still owed across the tenant's leases.</summary>
    public decimal Outstanding { get; set; }

    /// <summary>Sum of owed payments that are past their due date.</summary>
    public decimal Overdue { get; set; }

    /// <summary>Count of payments contributing to <see cref="Overdue"/>.</summary>
    public int OverdueCount { get; set; }

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => $"portal-balance-{TenantId}";
}

/// <summary>Tenant-facing projection of one of their payments.</summary>
public class PortalPaymentResponse
{
    public int Id { get; set; }
    public int LeaseId { get; set; }
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

/// <summary>Request to enroll a lease in autopay (setup-mode Checkout). Lease must be the tenant's own.</summary>
public class AutopayEnrollRequest
{
    public int LeaseId { get; set; }

    [MaxLength(2048)]
    public string? SuccessUrl { get; set; }

    [MaxLength(2048)]
    public string? CancelUrl { get; set; }
}

/// <summary>Request to cancel autopay for one of the tenant's leases.</summary>
public class AutopayCancelRequest
{
    public int LeaseId { get; set; }
}

/// <summary>The tenant's autopay enrollment status for a lease.</summary>
public class AutopayStatusResponse
{
    public int LeaseId { get; set; }
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
