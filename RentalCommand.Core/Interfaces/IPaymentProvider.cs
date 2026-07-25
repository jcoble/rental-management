namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over a payment processor (e.g. Stripe). Phase 0 defines the contract only;
/// a concrete integration lands in a later phase.
/// </summary>
public interface IPaymentProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>Charge an amount (minor units) in the given currency and return the result.</summary>
    Task<PaymentResult> ChargeAsync(
        long amountInMinorUnits,
        string currency,
        string paymentMethodToken,
        CancellationToken ct = default);

    /// <summary>Refund a previously captured charge (full refund when <paramref name="amountInMinorUnits"/> is null).</summary>
    Task<PaymentResult> RefundAsync(
        string providerChargeId,
        long? amountInMinorUnits = null,
        CancellationToken ct = default);
}

/// <summary>Outcome of a payment-provider operation.</summary>
public class PaymentResult
{
    public bool Success { get; set; }

    /// <summary>Provider-side identifier for the charge/refund.</summary>
    public string? ProviderChargeId { get; set; }

    /// <summary>Provider error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}
