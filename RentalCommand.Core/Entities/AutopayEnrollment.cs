namespace RentalCommand.Core.Entities;

/// <summary>
/// A tenant's enrollment in automatic rent payment for one of their leases. Created when a Stripe
/// Checkout <c>setup</c> session completes and a reusable payment method is saved off-session. The
/// Engine charges the saved customer + payment method when scheduled rent comes due.
/// At most one <see cref="Active"/> enrollment per lease.
/// </summary>
public class AutopayEnrollment
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public int TenantId { get; set; }

    /// <summary>Stripe customer that owns the saved payment method.</summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>Saved Stripe payment method charged off-session when rent is due.</summary>
    public string? StripePaymentMethodId { get; set; }

    /// <summary>True while autopay is live for this lease; set false on cancel.</summary>
    public bool Active { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
    public Tenant? Tenant { get; set; }
}
