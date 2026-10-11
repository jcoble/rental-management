using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Payments;

/// <summary>
/// Provider boundary for interactive payments. The durable payment attempt is the identity passed
/// through every method; implementations must return null only after a confirmed provider lookup
/// found no object. Transport/unknown failures must throw so the caller leaves the Submitted fence
/// in place for a later reconciliation.
/// </summary>
public interface IInteractivePaymentProviderClient
{
    Task<InteractiveProviderObject> CreatePaymentIntentAsync(
        InteractiveProviderCreateRequest request, CancellationToken ct);

    Task<InteractiveProviderObject> CreateCheckoutSessionAsync(
        InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
        CancellationToken ct);

    Task<InteractiveProviderObject> CreateSetupCheckoutSessionAsync(
        InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
        CancellationToken ct);

    Task<InteractiveProviderObject?> ReconcileAsync(
        InteractiveProviderAttempt attempt, CancellationToken ct);

    Task<InteractiveProviderObject?> CancelOrExpireAsync(
        InteractiveProviderAttempt attempt, CancellationToken ct);

    Task<(string? CustomerId, string? PaymentMethodId)> GetSetupPaymentMethodAsync(
        string setupIntentId, CancellationToken ct);
}

public sealed record InteractiveProviderAttempt(
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    TenantPaymentAttemptType AttemptType,
    string? ProviderObjectId,
    int PortfolioId,
    int TenantAccountId,
    decimal Amount,
    string Currency);

public sealed record InteractiveProviderCreateRequest(
    long PaymentAttemptId,
    string Provider,
    string IdempotencyKey,
    int PortfolioId,
    int TenantAccountId,
    long? ChargeLedgerEntryId,
    decimal Amount,
    string Currency,
    bool IsSetup,
    int? TenantId = null,
    int? AuthorizingPartyId = null,
    int? ActorUserId = null);

public sealed record InteractiveProviderObject(
    string ProviderPaymentId,
    string Status,
    string IdempotencyKey,
    string? CheckoutUrl = null,
    string? ClientSecret = null,
    string? PaymentIntentId = null,
    string? CheckoutSessionId = null,
    bool ConfirmedNoProviderObject = false);

/// <summary>Stripe implementation of the interactive provider seam.</summary>
public sealed class StripeInteractivePaymentProviderClient : IInteractivePaymentProviderClient
{
    private readonly StripeConfig _config;

    public StripeInteractivePaymentProviderClient(IOptions<StripeConfig> config) =>
        _config = config.Value;

    public async Task<InteractiveProviderObject> CreatePaymentIntentAsync(
        InteractiveProviderCreateRequest request, CancellationToken ct)
    {
        try
        {
            var intent = await new PaymentIntentService().CreateAsync(new PaymentIntentCreateOptions
            {
                Amount = (long)(request.Amount * 100m),
                Currency = request.Currency.ToLowerInvariant(),
                Metadata = Metadata(request),
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true,
                },
            }, Request(request.IdempotencyKey), ct);
            return new(intent.Id, intent.Status, request.IdempotencyKey,
                ClientSecret: intent.ClientSecret, PaymentIntentId: intent.Id);
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    public async Task<InteractiveProviderObject> CreateCheckoutSessionAsync(
        InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
        CancellationToken ct)
    {
        try
        {
            var session = await new SessionService().CreateAsync(new SessionCreateOptions
            {
                Mode = "payment",
                Expand = new List<string> { "payment_intent" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new()
                    {
                        Quantity = 1,
                        PriceData = new SessionLineItemPriceDataOptions
                        {
                            Currency = request.Currency.ToLowerInvariant(),
                            UnitAmount = (long)(request.Amount * 100m),
                            ProductData = new SessionLineItemPriceDataProductDataOptions
                            {
                                Name = "Tenant account payment",
                            },
                        },
                    },
                },
                Metadata = Metadata(request),
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    Metadata = Metadata(request),
                },
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
            }, Request(request.IdempotencyKey), ct);
            return new(
                session.PaymentIntentId ?? session.Id,
                session.PaymentStatus ?? "open",
                request.IdempotencyKey,
                CheckoutUrl: session.Url,
                PaymentIntentId: session.PaymentIntentId,
                CheckoutSessionId: session.Id);
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    public async Task<InteractiveProviderObject> CreateSetupCheckoutSessionAsync(
        InteractiveProviderCreateRequest request, string successUrl, string cancelUrl,
        CancellationToken ct)
    {
        try
        {
            var metadata = Metadata(request);
            metadata["autopay"] = "1";
            var session = await new SessionService().CreateAsync(new SessionCreateOptions
            {
                Mode = "setup",
                SetupIntentData = new SessionSetupIntentDataOptions
                {
                    Metadata = metadata,
                },
                Metadata = metadata,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
            }, Request(request.IdempotencyKey), ct);
            return new(session.Id, session.Status ?? "open", request.IdempotencyKey,
                CheckoutUrl: session.Url, CheckoutSessionId: session.Id);
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    public async Task<InteractiveProviderObject?> ReconcileAsync(
        InteractiveProviderAttempt attempt, CancellationToken ct)
    {
        try
        {
            if (attempt.AttemptType == TenantPaymentAttemptType.Verification)
                return await ReconcileSetupAsync(attempt, ct);

            // Checkout can durably expose its Session before Stripe has exposed a
            // PaymentIntent.  Keep the provider identity exact: a cs_ key must be
            // read as a Checkout Session, never sent to PaymentIntentService.
            if (attempt.ProviderObjectId?.StartsWith("cs_", StringComparison.Ordinal) == true)
            {
                var session = await new SessionService().GetAsync(
                    attempt.ProviderObjectId,
                    requestOptions: Request(attempt.IdempotencyKey),
                    cancellationToken: ct);
                return CheckoutSession(session, attempt.IdempotencyKey);
            }

            if (!string.IsNullOrWhiteSpace(attempt.ProviderObjectId))
            {
                var intent = await new PaymentIntentService().GetAsync(
                    attempt.ProviderObjectId,
                    requestOptions: new RequestOptions { ApiKey = _config.SecretKey },
                    cancellationToken: ct);
                return PaymentIntent(intent, attempt.IdempotencyKey);
            }

            var matches = await new PaymentIntentService().SearchAsync(
                new PaymentIntentSearchOptions
                {
                    Query = $"metadata['paymentAttemptId']:'{attempt.PaymentAttemptId}'",
                    Limit = 10,
                }, Request(attempt.IdempotencyKey), ct);
            var match = matches.Data.FirstOrDefault();
            if (match is not null)
                return PaymentIntent(match, attempt.IdempotencyKey);

            // A Checkout Session can be accepted before its PaymentIntent is
            // searchable.  Search the exact durable metadata before concluding
            // that no provider object exists.
            var sessions = await new SessionService().ListAsync(
                new SessionListOptions { Limit = 100 }, Request(attempt.IdempotencyKey), ct);
            var sessionMatch = sessions.Data.FirstOrDefault(candidate =>
                (candidate.Metadata ?? new Dictionary<string, string>())
                    .TryGetValue("paymentAttemptId", out var value)
                && value == attempt.PaymentAttemptId.ToString());
            return sessionMatch is null
                ? new(string.Empty, "none", attempt.IdempotencyKey,
                    ConfirmedNoProviderObject: true)
                : CheckoutSession(sessionMatch, attempt.IdempotencyKey);
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    private async Task<InteractiveProviderObject?> ReconcileSetupAsync(
        InteractiveProviderAttempt attempt, CancellationToken ct)
    {
        Session? session = null;
        if (!string.IsNullOrWhiteSpace(attempt.ProviderObjectId))
        {
            session = await new SessionService().GetAsync(
                attempt.ProviderObjectId,
                requestOptions: Request(attempt.IdempotencyKey),
                cancellationToken: ct);
        }
        else
        {
            var sessions = await new SessionService().ListAsync(
                new SessionListOptions { Limit = 100 }, Request(attempt.IdempotencyKey), ct);
            session = sessions.Data.FirstOrDefault(candidate =>
                (candidate.Metadata ?? new Dictionary<string, string>()).TryGetValue("paymentAttemptId", out var value)
                && value == attempt.PaymentAttemptId.ToString());
        }

        if (session is null)
            return new(string.Empty, "none", attempt.IdempotencyKey,
                ConfirmedNoProviderObject: true);
        if (string.IsNullOrWhiteSpace(session.SetupIntentId))
            return new(session.Id, session.Status ?? "open", attempt.IdempotencyKey,
                CheckoutSessionId: session.Id);
        var setupIntent = await new SetupIntentService().GetAsync(
            session.SetupIntentId, requestOptions: Request(attempt.IdempotencyKey),
            cancellationToken: ct);
        return new(session.Id, setupIntent.Status, attempt.IdempotencyKey,
            PaymentIntentId: setupIntent.Id, CheckoutSessionId: session.Id);
    }

    public async Task<InteractiveProviderObject?> CancelOrExpireAsync(
        InteractiveProviderAttempt attempt, CancellationToken ct)
    {
        try
        {
            if (attempt.AttemptType == TenantPaymentAttemptType.Verification)
            {
                var setup = await ReconcileSetupAsync(attempt, ct);
                if (setup?.PaymentIntentId is not { Length: > 0 } setupIntentId)
                    return setup;
                var canceledSetup = await new SetupIntentService().CancelAsync(
                    setupIntentId, requestOptions: Request(attempt.IdempotencyKey),
                    cancellationToken: ct);
                return new(setup!.ProviderPaymentId, canceledSetup.Status,
                    attempt.IdempotencyKey, CheckoutSessionId: setup.CheckoutSessionId,
                    PaymentIntentId: canceledSetup.Id);
            }

            var current = await ReconcileAsync(attempt, ct);
            if (current is null)
                return new(string.Empty, "canceled", attempt.IdempotencyKey,
                    ConfirmedNoProviderObject: true);
            if (current.Status is "succeeded" or "canceled") return current;

            if (!string.IsNullOrWhiteSpace(current.CheckoutSessionId)
                && current.CheckoutSessionId.StartsWith("cs_", StringComparison.Ordinal))
            {
                var expired = await new SessionService().ExpireAsync(
                    current.CheckoutSessionId,
                    options: null,
                    requestOptions: Request(attempt.IdempotencyKey),
                    cancellationToken: ct);
                return CheckoutSession(expired, attempt.IdempotencyKey);
            }

            var paymentIntentId = current.PaymentIntentId ?? current.ProviderPaymentId;
            if (!string.IsNullOrWhiteSpace(paymentIntentId)
                && paymentIntentId.StartsWith("pi_", StringComparison.Ordinal))
            {
                var canceled = await new PaymentIntentService().CancelAsync(
                    paymentIntentId, requestOptions: Request(attempt.IdempotencyKey),
                    cancellationToken: ct);
                return PaymentIntent(canceled, attempt.IdempotencyKey);
            }

            return current;
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    public async Task<(string? CustomerId, string? PaymentMethodId)> GetSetupPaymentMethodAsync(
        string setupIntentId, CancellationToken ct)
    {
        try
        {
            var setupIntent = await new SetupIntentService().GetAsync(
                setupIntentId,
                requestOptions: new RequestOptions { ApiKey = _config.SecretKey },
                cancellationToken: ct);
            return (setupIntent.CustomerId, setupIntent.PaymentMethodId);
        }
        catch (StripeException ex)
        {
            throw InteractiveProviderException.FromStripe(ex);
        }
    }

    private RequestOptions Request(string idempotencyKey) => new()
    {
        ApiKey = _config.SecretKey,
        IdempotencyKey = idempotencyKey,
    };

    private static Dictionary<string, string> Metadata(InteractiveProviderCreateRequest request) =>
        new()
        {
            ["portfolioId"] = request.PortfolioId.ToString(),
            ["tenantAccountId"] = request.TenantAccountId.ToString(),
            ["paymentAttemptId"] = request.PaymentAttemptId.ToString(),
            ["chargeLedgerEntryId"] = request.ChargeLedgerEntryId?.ToString() ?? string.Empty,
            ["tenantId"] = request.TenantId?.ToString() ?? string.Empty,
            ["authorizingPartyId"] = request.AuthorizingPartyId?.ToString() ?? string.Empty,
            ["actorUserId"] = request.ActorUserId?.ToString() ?? string.Empty,
        };

    private static InteractiveProviderObject PaymentIntent(
        PaymentIntent intent, string idempotencyKey) => new(
            intent.Id, intent.Status, idempotencyKey,
            ClientSecret: intent.ClientSecret, PaymentIntentId: intent.Id);

    private static InteractiveProviderObject CheckoutSession(
        Session session, string idempotencyKey) => new(
            session.Id,
            session.PaymentStatus ?? session.Status ?? "open",
            idempotencyKey,
            CheckoutUrl: session.Url,
            PaymentIntentId: session.PaymentIntentId,
            CheckoutSessionId: session.Id);
}

public sealed class InteractiveProviderException : Exception
{
    public bool IsDefinitive { get; }
    public string? FailureCode { get; }

    public InteractiveProviderException(string message, bool isDefinitive = false,
        string? failureCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsDefinitive = isDefinitive;
        FailureCode = failureCode;
    }

    public static InteractiveProviderException FromStripe(StripeException exception) => new(
        exception.Message,
        exception.StripeError?.Type is "card_error" or "invalid_request_error"
            && exception.StripeError?.Code is not "idempotency_key_in_use",
        exception.StripeError?.Code,
        exception);
}
