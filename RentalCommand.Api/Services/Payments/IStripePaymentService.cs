namespace RentalCommand.Api.Services.Payments;

/// <summary>
/// Handles Stripe PaymentIntent creation and webhook event processing.
/// All methods are gated: when Stripe is not configured (<see cref="CreateIntentResult.Outcome.NotEnabled"/>),
/// they return gracefully without throwing.
/// </summary>
public interface IStripePaymentService
{
    /// <summary>
    /// Creates a Stripe PaymentIntent for the given payment and persists a pending
    /// <c>PaymentTransaction</c>. Returns <see cref="CreateIntentResult"/> — check
    /// <see cref="CreateIntentResult.Result"/> before using the client secret.
    /// </summary>
    Task<CreateIntentResult> CreatePaymentIntentAsync(int portfolioId, int paymentId, CancellationToken ct);

    /// <summary>
    /// Verifies the Stripe webhook signature and processes the event. Idempotent — duplicate
    /// deliveries are detected and skipped. Throws <see cref="Stripe.StripeException"/> on bad
    /// signature (caller should return 400).
    /// </summary>
    Task HandleWebhookEventAsync(string json, string signature, CancellationToken ct);
}

/// <summary>Result returned by <see cref="IStripePaymentService.CreatePaymentIntentAsync"/>.</summary>
public class CreateIntentResult
{
    public enum Outcome { Ok, NotEnabled, NotFound }

    public Outcome Result { get; init; }
    public string? ClientSecret { get; init; }
    public string? PublishableKey { get; init; }
    public int? TransactionId { get; init; }

    public static CreateIntentResult NotEnabled() => new() { Result = Outcome.NotEnabled };
    public static CreateIntentResult NotFound() => new() { Result = Outcome.NotFound };
    public static CreateIntentResult Ok(string clientSecret, string? publishableKey, int transactionId) =>
        new() { Result = Outcome.Ok, ClientSecret = clientSecret, PublishableKey = publishableKey, TransactionId = transactionId };
}
