using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Payments;

/// <inheritdoc cref="IStripePaymentService"/>
public class StripePaymentService : IStripePaymentService
{
    // Checkout/SetupIntent metadata keys — carried by Stripe back to the webhook so we can
    // resolve which Payment was paid / which lease enrolled in autopay.
    private const string MetadataPaymentId = "paymentId";
    private const string MetadataPortfolioId = "portfolioId";
    private const string MetadataLeaseId = "leaseId";
    private const string MetadataTenantId = "tenantId";
    private const string MetadataAutopay = "autopay";

    private const string DefaultSuccessUrl = "/portal/payments?checkout=success";
    private const string DefaultCancelUrl = "/portal/payments?checkout=cancel";

    private readonly RentalCommandDbContext _db;
    private readonly StripeConfig _config;
    private readonly ISandboxGuard _sandbox;
    private readonly ILogger<StripePaymentService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomicUnitOfWork;

    private static readonly AtomicJsonResultCodec<PrepareProviderPaymentCreateResult> PrepareCodec =
        new("prepare-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<RecordVerifiedProviderPaymentEventResult> ProviderEventCodec =
        new("record-verified-provider-payment-event-result.v1");

    public StripePaymentService(
        RentalCommandDbContext db,
        IOptions<StripeConfig> config,
        ISandboxGuard sandbox,
        ILogger<StripePaymentService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomicUnitOfWork)
    {
        _db = db;
        _config = config.Value;
        _sandbox = sandbox;
        _logger = logger;
        _timeProvider = timeProvider;
        _atomicUnitOfWork = atomicUnitOfWork;
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnlinePaymentsAvailableAsync(int portfolioId, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            return false;
        }

        return !await _sandbox.IsSandboxAsync(portfolioId, ct);
    }

    /// <inheritdoc/>
    public async Task<CreateIntentResult> CreatePaymentIntentAsync(int portfolioId, int paymentId, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — create-intent is a no-op");
            return CreateIntentResult.NotEnabled();
        }

        // HARD sandbox guard: never move real money for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] create-intent for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CreateIntentResult.NotEnabled();
        }

        var payment = await _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.PortfolioId == portfolioId, ct);

        if (payment == null)
        {
            return CreateIntentResult.NotFound();
        }

        var idempotencyKey = BuildPaymentIdempotencyKey("intent", payment);
        var prepared = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", idempotencyKey),
            new PrepareProviderPaymentCreateCommand(
                portfolioId,
                paymentId,
                TenantId: null,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "usd",
                PreparedAtUtc: _timeProvider.UtcNow()),
            PrepareCodec,
            ct);
        if (prepared.Value.Outcome == PrepareProviderPaymentCreateOutcome.NotFound)
        {
            return CreateIntentResult.NotFound();
        }

        var requestOptions = new RequestOptions
        {
            ApiKey = _config.SecretKey,
            // Deterministic per payment + period: a retried/double-submitted create returns the
            // original PaymentIntent rather than minting a second one for the same rent obligation.
            IdempotencyKey = idempotencyKey,
        };
        var intentService = new PaymentIntentService();
        var intent = await intentService.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)(prepared.Value.Amount * 100),
            Currency = "usd",
            Metadata = new Dictionary<string, string>
            {
                ["paymentId"] = paymentId.ToString(),
                ["portfolioId"] = portfolioId.ToString()
            },
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
            {
                Enabled = true
            }
        }, requestOptions, ct);

        await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", idempotencyKey),
            new FinalizeProviderPaymentCreateCommand(
                portfolioId,
                paymentId,
                prepared.Value.PaymentTransactionId,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                ProviderPaymentId: intent.Id,
                Status: PaymentTransactionStatus.Pending,
                FailureReason: null,
                RecordedAtUtc: _timeProvider.UtcNow()),
            FinalizeCodec,
            ct);

        _logger.LogInformation(
            "Created Stripe PaymentIntent {IntentId} for payment {PaymentId} (portfolio {PortfolioId})",
            intent.Id, paymentId, portfolioId);

        return CreateIntentResult.Ok(
            intent.ClientSecret!,
            _config.PublishableKey,
            prepared.Value.PaymentTransactionId);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreatePaymentCheckoutSessionAsync(
        int portfolioId, int tenantId, int paymentId, string? successUrl, string? cancelUrl, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — payment checkout is a no-op");
            return CheckoutResult.NotEnabled();
        }

        // HARD sandbox guard: never create a real Checkout session for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] payment checkout for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CheckoutResult.NotEnabled();
        }

        // OWNERSHIP: the payment must be in this portfolio AND on a lease belonging to the calling
        // tenant. A payment on someone else's lease is simply "not found" — a tenant can never pay,
        // or even probe the existence of, another tenant's rent (IDOR guard).
        var payment = await _db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.Id == paymentId
                     && p.PortfolioId == portfolioId
                     && _db.Leases.Any(l => l.Id == p.LeaseId && l.TenantId == tenantId),
                ct);

        if (payment == null)
        {
            return CheckoutResult.NotFound();
        }

        var description = payment.PaymentType == PaymentType.Rent
            ? $"Rent payment{(payment.PeriodKey != null ? $" — {payment.PeriodKey}" : "")}"
            : $"{payment.PaymentType} payment";

        var idempotencyKey = BuildPaymentIdempotencyKey("checkout", payment);
        var prepared = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", idempotencyKey),
            new PrepareProviderPaymentCreateCommand(
                portfolioId,
                paymentId,
                tenantId,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "usd",
                PreparedAtUtc: _timeProvider.UtcNow()),
            PrepareCodec,
            ct);
        if (prepared.Value.Outcome == PrepareProviderPaymentCreateOutcome.NotFound)
        {
            return CheckoutResult.NotFound();
        }

        var requestOptions = new RequestOptions
        {
            ApiKey = _config.SecretKey,
            // Deterministic per payment + period so a retried checkout returns the original session
            // for the same rent obligation instead of opening (and potentially charging via) a second.
            IdempotencyKey = idempotencyKey,
        };
        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            // Card AND ACH bank debit, so a tenant can choose either at the hosted page.
            PaymentMethodTypes = new List<string> { "card", "us_bank_account" },
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = (long)(prepared.Value.Amount * 100),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = description,
                        },
                    },
                },
            },
            // Carried back on checkout.session.completed so we can resolve + mark the Payment Paid.
            Metadata = new Dictionary<string, string>
            {
                [MetadataPaymentId] = paymentId.ToString(),
                [MetadataPortfolioId] = portfolioId.ToString(),
            },
            SuccessUrl = ResolveSuccessUrl(successUrl),
            CancelUrl = ResolveCancelUrl(cancelUrl),
        }, requestOptions, ct);

        await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", idempotencyKey),
            new FinalizeProviderPaymentCreateCommand(
                portfolioId,
                paymentId,
                prepared.Value.PaymentTransactionId,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                ProviderPaymentId: session.Id,
                Status: PaymentTransactionStatus.Pending,
                FailureReason: null,
                RecordedAtUtc: _timeProvider.UtcNow()),
            FinalizeCodec,
            ct);

        _logger.LogInformation(
            "Created Stripe Checkout session {SessionId} for payment {PaymentId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.Id, paymentId, portfolioId, tenantId);

        return CheckoutResult.Ok(session.Url!);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreateAutopaySetupSessionAsync(
        int portfolioId, int tenantId, int leaseId, string? successUrl, string? cancelUrl, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — autopay setup is a no-op");
            return CheckoutResult.NotEnabled();
        }

        // HARD sandbox guard: never enroll a real payment method for a demo account.
        if (await _sandbox.IsSandboxAsync(portfolioId, ct))
        {
            _logger.LogInformation("[suppressed — sandbox] autopay setup for portfolio {PortfolioId} — Stripe not called.", portfolioId);
            return CheckoutResult.NotEnabled();
        }

        // OWNERSHIP: lease must be the calling tenant's own lease in this portfolio.
        var lease = await _db.Leases
            .FirstOrDefaultAsync(
                l => l.Id == leaseId && l.PortfolioId == portfolioId && l.TenantId == tenantId, ct);

        if (lease == null)
        {
            return CheckoutResult.NotFound();
        }

        var requestOptions = new RequestOptions { ApiKey = _config.SecretKey };
        var sessionService = new SessionService();
        var session = await sessionService.CreateAsync(new SessionCreateOptions
        {
            Mode = "setup",
            PaymentMethodTypes = new List<string> { "card", "us_bank_account" },
            // Carried back on checkout.session.completed so we know which lease/tenant enrolled.
            SetupIntentData = new SessionSetupIntentDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    [MetadataAutopay] = "1",
                    [MetadataLeaseId] = leaseId.ToString(),
                    [MetadataTenantId] = tenantId.ToString(),
                    [MetadataPortfolioId] = portfolioId.ToString(),
                },
            },
            Metadata = new Dictionary<string, string>
            {
                [MetadataAutopay] = "1",
                [MetadataLeaseId] = leaseId.ToString(),
                [MetadataTenantId] = tenantId.ToString(),
                [MetadataPortfolioId] = portfolioId.ToString(),
            },
            SuccessUrl = ResolveSuccessUrl(successUrl),
            CancelUrl = ResolveCancelUrl(cancelUrl),
        }, requestOptions, ct);

        _logger.LogInformation(
            "Created Stripe autopay setup session {SessionId} for lease {LeaseId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.Id, leaseId, portfolioId, tenantId);

        return CheckoutResult.Ok(session.Url!);
    }

    /// <inheritdoc/>
    public async Task HandleWebhookEventAsync(string json, string signature, CancellationToken ct)
    {
        // Throws StripeException on bad signature — let it propagate to the controller (→ 400).
        // throwOnApiVersionMismatch: false so a Stripe account pinned to a different API version than
        // this SDK build doesn't hard-fail every webhook. We only read a small, stable set of fields
        // (event id/type, session/intent id, payment_status, metadata) that are version-agnostic.
        var ev = EventUtility.ConstructEvent(json, signature, _config.WebhookSecret, throwOnApiVersionMismatch: false);
        var now = _timeProvider.UtcNow();
        var command = await NormalizeVerifiedEventAsync(ev, now, ct);
        var outcome = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-event.record", $"stripe:{ev.Id}"),
            command,
            ProviderEventCodec,
            ct);

        _logger.LogInformation(
            "Stripe webhook event {EventId} ({EventType}) completed with {Outcome}.",
            ev.Id,
            ev.Type,
            outcome.Value.Outcome);
    }

    private async Task<RecordVerifiedProviderPaymentEventCommand> NormalizeVerifiedEventAsync(
        Event ev,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (ev.Data.Object is PaymentIntent intent &&
            ev.Type is "payment_intent.succeeded" or "payment_intent.payment_failed" or "payment_intent.canceled")
        {
            var kind = ev.Type switch
            {
                "payment_intent.succeeded" => ProviderPaymentEventKind.Succeeded,
                "payment_intent.payment_failed" => ProviderPaymentEventKind.Failed,
                _ => ProviderPaymentEventKind.Canceled,
            };
            return new RecordVerifiedProviderPaymentEventCommand(
                "stripe",
                ev.Id,
                ev.Type,
                JsonSerializer.Serialize(new
                {
                    intent.Id,
                    intent.Status,
                    intent.Amount,
                    intent.Currency,
                }),
                intent.Id,
                kind,
                intent.Amount / 100m,
                intent.Currency,
                intent.LastPaymentError?.Message,
                receivedAtUtc,
                receivedAtUtc);
        }

        if (ev.Data.Object is Session session && ev.Type == "checkout.session.completed")
        {
            var metadata = session.Metadata ?? new Dictionary<string, string>();
            var isSetup = session.Mode == "setup" ||
                (metadata.TryGetValue(MetadataAutopay, out var autopay) && autopay == "1");
            if (!isSetup)
            {
                return new RecordVerifiedProviderPaymentEventCommand(
                    "stripe",
                    ev.Id,
                    ev.Type,
                    JsonSerializer.Serialize(new
                    {
                        session.Id,
                        session.PaymentIntentId,
                        session.PaymentStatus,
                        session.Mode,
                    }),
                    session.Id,
                    string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase)
                        ? ProviderPaymentEventKind.Succeeded
                        : ProviderPaymentEventKind.Pending,
                    session.AmountTotal is long amount ? amount / 100m : null,
                    session.Currency,
                    null,
                    receivedAtUtc,
                    receivedAtUtc);
            }

            metadata.TryGetValue(MetadataLeaseId, out var leaseRaw);
            metadata.TryGetValue(MetadataTenantId, out var tenantRaw);
            metadata.TryGetValue(MetadataPortfolioId, out var portfolioRaw);
            _ = int.TryParse(leaseRaw, out var leaseId);
            _ = int.TryParse(tenantRaw, out var tenantId);
            _ = int.TryParse(portfolioRaw, out var portfolioId);

            string? customerId = session.CustomerId;
            string? paymentMethodId = null;
            if (!string.IsNullOrWhiteSpace(session.SetupIntentId))
            {
                var setupIntent = await new SetupIntentService().GetAsync(
                    session.SetupIntentId,
                    requestOptions: new RequestOptions { ApiKey = _config.SecretKey },
                    cancellationToken: ct);
                paymentMethodId = setupIntent.PaymentMethodId;
                customerId ??= setupIntent.CustomerId;
            }

            return new RecordVerifiedProviderPaymentEventCommand(
                "stripe",
                ev.Id,
                ev.Type,
                JsonSerializer.Serialize(new
                {
                    session.Id,
                    session.SetupIntentId,
                    session.Mode,
                    portfolioId,
                    leaseId,
                    tenantId,
                }),
                session.Id,
                ProviderPaymentEventKind.SetupCompleted,
                null,
                null,
                null,
                receivedAtUtc,
                receivedAtUtc,
                portfolioId > 0 ? portfolioId : null,
                leaseId > 0 ? leaseId : null,
                tenantId > 0 ? tenantId : null,
                customerId,
                paymentMethodId);
        }

        return new RecordVerifiedProviderPaymentEventCommand(
            "stripe",
            ev.Id,
            ev.Type,
            JsonSerializer.Serialize(new { ev.Id, ev.Type }),
            $"event:{ev.Id}",
            ProviderPaymentEventKind.Ignored,
            null,
            null,
            null,
            receivedAtUtc,
            receivedAtUtc);
    }

    /// <summary>
    /// Resolves the success URL: an explicit request value is honored ONLY when it passes the
    /// return-URL allowlist (so a client can't turn Checkout into an open redirect to a phishing
    /// site); otherwise the trusted configured value, else a built-in.
    /// </summary>
    private string ResolveSuccessUrl(string? requested) =>
        IsAllowedReturnUrl(requested) ? requested!
        : !string.IsNullOrWhiteSpace(_config.CheckoutSuccessUrl) ? _config.CheckoutSuccessUrl!
        : DefaultSuccessUrl;

    /// <summary>Resolves the cancel URL with the same allowlist guard as the success URL.</summary>
    private string ResolveCancelUrl(string? requested) =>
        IsAllowedReturnUrl(requested) ? requested!
        : !string.IsNullOrWhiteSpace(_config.CheckoutCancelUrl) ? _config.CheckoutCancelUrl!
        : DefaultCancelUrl;

    /// <summary>
    /// A client-supplied return URL is trusted only if it is an absolute URL whose host is
    /// explicitly allowed: either it appears in <see cref="StripeConfig.AllowedReturnHosts"/>
    /// (https only), or it is a loopback host (localhost/127.0.0.1/::1) for local development.
    /// Anything else (other hosts, non-http schemes, relative/garbage) is rejected so we fall back
    /// to the server-configured URL — closing the open-redirect vector.
    /// </summary>
    private bool IsAllowedReturnUrl(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            !Uri.TryCreate(requested, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.IsLoopback &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true; // local dev (localhost / 127.0.0.1 / ::1)
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && _config.AllowedReturnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deterministic Stripe idempotency key for a user-initiated money-moving create call, scoped by
    /// the create kind (intent vs checkout), the payment, and its billing period. A retried or
    /// double-submitted create for the same rent obligation reuses the original Stripe object instead
    /// of creating a second; Stripe expires idempotency keys after 24h, so a genuinely new attempt for
    /// the same payment later still proceeds. Falls back to the due date when a payment has no PeriodKey.
    /// </summary>
    private static string BuildPaymentIdempotencyKey(string kind, Payment payment)
    {
        var period = string.IsNullOrEmpty(payment.PeriodKey)
            ? payment.DueDate.ToString("yyyyMMdd")
            : payment.PeriodKey;
        return $"{kind}-{payment.Id}-{period}";
    }
}
