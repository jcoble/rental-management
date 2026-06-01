namespace RentalCommand.Api.DTOs;

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

/// <summary>Tenant-facing projection of a portal message thread (created by the tenant, replied by staff).</summary>
public class PortalMessageResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>Portal author; null for landlord-initiated messages addressed to this tenant.</summary>
    public int? UserAccountId { get; set; }

    /// <summary>True when this message was sent by the landlord to the tenant (inbound to the tenant).</summary>
    public bool FromLandlord { get; set; }

    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>portal-message-1</c>.</summary>
    public string TestId => $"portal-message-{Id}";
}
