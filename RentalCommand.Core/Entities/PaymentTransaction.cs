using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Records a single Stripe payment attempt for a <see cref="Payment"/>. One payment may have
/// multiple transactions if retries occur. Tracks the Stripe PaymentIntent lifecycle.
/// </summary>
public class PaymentTransaction
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "usd";
    public string Provider { get; set; } = "stripe";
    public string? ProviderPaymentIntentId { get; set; }

    /// <summary>
    /// Deterministic key sent to the payment provider as its idempotency key for the create call that
    /// produced this transaction, and used locally to recover the row after a crash. For autopay this
    /// is derived from the payment id + billing period, so a retry of the same charge (a) returns the
    /// original provider intent instead of charging again and (b) can be matched back to this row even
    /// if the row's <see cref="ProviderPaymentIntentId"/> was never persisted. Null for legacy rows.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public PaymentTransactionStatus Status { get; set; } = PaymentTransactionStatus.Pending;
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Payment? Payment { get; set; }
}
