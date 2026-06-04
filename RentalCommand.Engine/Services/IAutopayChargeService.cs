namespace RentalCommand.Engine.Services;

/// <summary>
/// Charges scheduled rent off-session for leases enrolled in autopay. For each due
/// <see cref="Core.Entities.Payment"/> on an Active <see cref="Core.Entities.AutopayEnrollment"/>,
/// creates a confirmed off-session Stripe PaymentIntent against the saved customer + payment method.
/// The API's existing <c>payment_intent.succeeded</c> webhook marks the payment Paid.
/// Gated on Stripe being configured; idempotent (never double-charges a period).
/// </summary>
public interface IAutopayChargeService
{
    /// <summary>
    /// Scan scheduled, unpaid, due rent payments whose lease has an Active autopay enrollment and
    /// charge each off-session. Returns the number of charges initiated this run. No-op (returns 0)
    /// when Stripe is not configured.
    /// </summary>
    Task<int> ChargeDueAsync(CancellationToken ct = default);
}
