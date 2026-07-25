namespace RentalCommand.Api.Services.Payments;

/// <summary>
/// Handles Stripe PaymentIntent creation and webhook event processing.
/// All methods are gated: when Stripe is not configured (<see cref="CreateIntentResult.Outcome.NotEnabled"/>),
/// they return gracefully without throwing.
/// </summary>
public interface IStripePaymentService : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>
    /// True when hosted online payments may be offered for the portfolio. This includes the global
    /// Stripe configuration gate and the portfolio sandbox guard used by Checkout creation.
    /// </summary>
    Task<bool> IsOnlinePaymentsAvailableAsync(int portfolioId, CancellationToken ct);

    /// <summary>
    /// Creates a Stripe PaymentIntent for an open canonical tenant-account charge and persists a
    /// durable <c>TenantPaymentAttempt</c>. Returns <see cref="CreateIntentResult"/> — check
    /// <see cref="CreateIntentResult.Result"/> before using the client secret.
    /// </summary>
    Task<CreateIntentResult> CreatePaymentIntentAsync(
        int portfolioId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId, CancellationToken ct);

    /// <summary>
    /// Tenant-safe hosted-Checkout path for paying one open <c>TenantLedgerEntry</c>. Verifies the
    /// account belongs to the calling tenant (otherwise <see cref="CheckoutResult.Outcome.NotFound"/>),
    /// then creates a Stripe Checkout Session and a pending <c>TenantPaymentAttempt</c>.
    /// Gated: returns <see cref="CheckoutResult.Outcome.NotEnabled"/> when Stripe is not configured.
    /// </summary>
    Task<CheckoutResult> CreatePaymentCheckoutSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId,
        string? successUrl, string? cancelUrl, CancellationToken ct);

    /// <summary>
    /// Creates a Stripe Checkout Session in <c>setup</c> mode so the tenant saves a reusable payment
    /// method for off-session autopay on the given tenant account. Verifies the account belongs to
    /// the calling tenant (otherwise <see cref="CheckoutResult.Outcome.NotFound"/>). Gated.
    /// </summary>
    Task<CheckoutResult> CreateAutopaySetupSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, int actorUserId, string operationKey,
        string? successUrl, string? cancelUrl, CancellationToken ct);

    /// <summary>
    /// Verifies the Stripe webhook signature and processes the event. Idempotent — duplicate
    /// deliveries are detected and skipped. Throws <see cref="Stripe.StripeException"/> on bad
    /// signature (caller should return 400).
    /// </summary>
    Task HandleWebhookEventAsync(string json, string signature, CancellationToken ct);
}

/// <summary>Result returned by the hosted-Checkout creation methods.</summary>
public class CheckoutResult
{
    public enum Outcome { Ok, NotEnabled, NotFound }

    public Outcome Result { get; init; }
    public string? CheckoutUrl { get; init; }

    public static CheckoutResult NotEnabled() => new() { Result = Outcome.NotEnabled };
    public static CheckoutResult NotFound() => new() { Result = Outcome.NotFound };
    public static CheckoutResult Ok(string checkoutUrl) => new() { Result = Outcome.Ok, CheckoutUrl = checkoutUrl };
}

/// <summary>Result returned by <see cref="IStripePaymentService.CreatePaymentIntentAsync"/>.</summary>
public class CreateIntentResult
{
    public enum Outcome { Ok, NotEnabled, NotFound }

    public Outcome Result { get; init; }
    public string? ClientSecret { get; init; }
    public string? PublishableKey { get; init; }
    public long? PaymentAttemptId { get; init; }

    public static CreateIntentResult NotEnabled() => new() { Result = Outcome.NotEnabled };
    public static CreateIntentResult NotFound() => new() { Result = Outcome.NotFound };
    public static CreateIntentResult Ok(string clientSecret, string? publishableKey, long paymentAttemptId) =>
        new() { Result = Outcome.Ok, ClientSecret = clientSecret, PublishableKey = publishableKey,
            PaymentAttemptId = paymentAttemptId };
}
