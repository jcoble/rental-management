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
    public PaymentTransactionStatus Status { get; set; } = PaymentTransactionStatus.Pending;
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Payment? Payment { get; set; }
}
