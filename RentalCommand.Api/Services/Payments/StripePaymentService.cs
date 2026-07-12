using System.Text.Json;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;

namespace RentalCommand.Api.Services.Payments;

/// <inheritdoc cref="IStripePaymentService"/>
public class StripePaymentService : IStripePaymentService
{
    // Canonical account/attempt facts carried back to the signature-verified webhook.
    private const string MetadataTenantAccountId = "tenantAccountId";
    private const string MetadataChargeLedgerEntryId = "chargeLedgerEntryId";
    private const string MetadataPaymentAttemptId = "paymentAttemptId";
    private const string MetadataPortfolioId = "portfolioId";
    private const string MetadataAuthorizingPartyId = "authorizingPartyId";
    private const string MetadataActorUserId = "actorUserId";
    private const string MetadataTenantId = "tenantId";
    private const string MetadataAutopay = "autopay";

    private const string DefaultSuccessUrl = "/portal/payments?checkout=success";
    private const string DefaultCancelUrl = "/portal/payments?checkout=cancel";

    private readonly StripeConfig _config;
    private readonly ISandboxGuard _sandbox;
    private readonly ILogger<StripePaymentService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomicUnitOfWork;

    private static readonly AtomicJsonResultCodec<PrepareProviderPaymentCreateResult> PrepareCodec =
        new("prepare-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<PrepareProviderAutopaySetupResult> PrepareSetupCodec =
        new("prepare-provider-autopay-setup-result.v1");
    private static readonly AtomicJsonResultCodec<RecordVerifiedProviderPaymentEventResult> ProviderEventCodec =
        new("record-verified-provider-payment-event-result.v1");

    public StripePaymentService(
        IOptions<StripeConfig> config,
        ISandboxGuard sandbox,
        ILogger<StripePaymentService> logger,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomicUnitOfWork)
    {
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
    public async Task<CreateIntentResult> CreatePaymentIntentAsync(
        int portfolioId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId, CancellationToken ct)
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

        var idempotencyKey = BuildPaymentIdempotencyKey("intent", chargeLedgerEntryId);
        var prepared = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", idempotencyKey),
            new PrepareProviderPaymentCreateCommand(
                portfolioId,
                tenantAccountId,
                chargeLedgerEntryId,
                actorUserId,
                TenantId: null,
                AutopayEnrollmentId: null,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "USD",
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
                [MetadataTenantAccountId] = tenantAccountId.ToString(),
                [MetadataChargeLedgerEntryId] = chargeLedgerEntryId.ToString(),
                [MetadataPaymentAttemptId] = prepared.Value.PaymentAttemptId.ToString(),
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
                tenantAccountId,
                prepared.Value.PaymentAttemptId,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                ProviderPaymentId: intent.Id,
                State: TenantPaymentAttemptState.Submitted,
                FailureReason: null,
                RecordedAtUtc: _timeProvider.UtcNow()),
            FinalizeCodec,
            ct);

        _logger.LogInformation(
            "Created Stripe PaymentIntent {IntentId} for tenant account {TenantAccountId} charge {ChargeId} (portfolio {PortfolioId})",
            intent.Id, tenantAccountId, chargeLedgerEntryId, portfolioId);

        return CreateIntentResult.Ok(
            intent.ClientSecret!,
            _config.PublishableKey,
            prepared.Value.PaymentAttemptId);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreatePaymentCheckoutSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, long chargeLedgerEntryId, int actorUserId,
        string? successUrl, string? cancelUrl, CancellationToken ct)
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

        var idempotencyKey = BuildPaymentIdempotencyKey("checkout", chargeLedgerEntryId);
        var prepared = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.prepare", idempotencyKey),
            new PrepareProviderPaymentCreateCommand(
                portfolioId,
                tenantAccountId,
                chargeLedgerEntryId,
                actorUserId,
                tenantId,
                AutopayEnrollmentId: null,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                Currency: "USD",
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
                            Name = "Tenant account payment",
                        },
                    },
                },
            },
            // Carried back on checkout.session.completed so we can resolve + mark the Payment Paid.
            Metadata = new Dictionary<string, string>
            {
                [MetadataTenantAccountId] = tenantAccountId.ToString(),
                [MetadataChargeLedgerEntryId] = chargeLedgerEntryId.ToString(),
                [MetadataPaymentAttemptId] = prepared.Value.PaymentAttemptId.ToString(),
                [MetadataPortfolioId] = portfolioId.ToString(),
            },
            SuccessUrl = ResolveSuccessUrl(successUrl),
            CancelUrl = ResolveCancelUrl(cancelUrl),
        }, requestOptions, ct);

        await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", idempotencyKey),
            new FinalizeProviderPaymentCreateCommand(
                portfolioId,
                tenantAccountId,
                prepared.Value.PaymentAttemptId,
                Provider: "stripe",
                IdempotencyKey: idempotencyKey,
                ProviderPaymentId: session.Id,
                State: TenantPaymentAttemptState.Submitted,
                FailureReason: null,
                RecordedAtUtc: _timeProvider.UtcNow()),
            FinalizeCodec,
            ct);

        _logger.LogInformation(
            "Created Stripe Checkout session {SessionId} for tenant account {TenantAccountId} charge {ChargeId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.Id, tenantAccountId, chargeLedgerEntryId, portfolioId, tenantId);

        return CheckoutResult.Ok(session.Url!);
    }

    /// <inheritdoc/>
    public async Task<CheckoutResult> CreateAutopaySetupSessionAsync(
        int portfolioId, int tenantId, int tenantAccountId, int actorUserId, string operationKey,
        string? successUrl, string? cancelUrl, CancellationToken ct)
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

        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var idempotencyKey = $"autopay-setup:{operationKey.Trim()}";
        if (idempotencyKey.Length > 200)
            throw new ArgumentException("Autopay setup operation key cannot exceed 186 characters.", nameof(operationKey));
        var prepared = await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-autopay.prepare", idempotencyKey),
            new PrepareProviderAutopaySetupCommand(portfolioId, tenantAccountId, tenantId,
                actorUserId, "stripe", idempotencyKey, "USD", _timeProvider.UtcNow()),
            PrepareSetupCodec,
            ct);
        if (prepared.Value.Outcome == PrepareProviderAutopaySetupOutcome.NotFound)
        {
            return CheckoutResult.NotFound();
        }

        var requestOptions = new RequestOptions { ApiKey = _config.SecretKey, IdempotencyKey = idempotencyKey };
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
                    [MetadataTenantAccountId] = tenantAccountId.ToString(),
                    [MetadataAuthorizingPartyId] = prepared.Value.AuthorizingPartyId.ToString(),
                    [MetadataActorUserId] = actorUserId.ToString(),
                    [MetadataPaymentAttemptId] = prepared.Value.PaymentAttemptId.ToString(),
                    [MetadataTenantId] = tenantId.ToString(),
                    [MetadataPortfolioId] = portfolioId.ToString(),
                },
            },
            Metadata = new Dictionary<string, string>
            {
                [MetadataAutopay] = "1",
                [MetadataTenantAccountId] = tenantAccountId.ToString(),
                [MetadataAuthorizingPartyId] = prepared.Value.AuthorizingPartyId.ToString(),
                [MetadataActorUserId] = actorUserId.ToString(),
                [MetadataPaymentAttemptId] = prepared.Value.PaymentAttemptId.ToString(),
                [MetadataTenantId] = tenantId.ToString(),
                [MetadataPortfolioId] = portfolioId.ToString(),
            },
            SuccessUrl = ResolveSuccessUrl(successUrl),
            CancelUrl = ResolveCancelUrl(cancelUrl),
        }, requestOptions, ct);

        await _atomicUnitOfWork.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-create.finalize", idempotencyKey),
            new FinalizeProviderPaymentCreateCommand(portfolioId, tenantAccountId,
                prepared.Value.PaymentAttemptId, "stripe", idempotencyKey, session.Id,
                TenantPaymentAttemptState.Submitted, null, _timeProvider.UtcNow()),
            FinalizeCodec,
            ct);

        _logger.LogInformation(
            "Created Stripe autopay setup session {SessionId} for tenant account {TenantAccountId} (portfolio {PortfolioId}, tenant {TenantId})",
            session.Id, tenantAccountId, portfolioId, tenantId);

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

            metadata.TryGetValue(MetadataTenantAccountId, out var accountRaw);
            metadata.TryGetValue(MetadataAuthorizingPartyId, out var partyRaw);
            metadata.TryGetValue(MetadataActorUserId, out var actorRaw);
            metadata.TryGetValue(MetadataPaymentAttemptId, out var attemptRaw);
            metadata.TryGetValue(MetadataTenantId, out var tenantRaw);
            metadata.TryGetValue(MetadataPortfolioId, out var portfolioRaw);
            _ = int.TryParse(accountRaw, out var tenantAccountId);
            _ = int.TryParse(partyRaw, out var authorizingPartyId);
            _ = int.TryParse(actorRaw, out var actorUserId);
            _ = long.TryParse(attemptRaw, out var paymentAttemptId);
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
                    tenantAccountId,
                    authorizingPartyId,
                    actorUserId,
                    paymentAttemptId,
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
                tenantAccountId > 0 ? tenantAccountId : null,
                authorizingPartyId > 0 ? authorizingPartyId : null,
                actorUserId > 0 ? actorUserId : null,
                paymentAttemptId > 0 ? paymentAttemptId : null,
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
    private static string BuildPaymentIdempotencyKey(string kind, long chargeLedgerEntryId) =>
        $"{kind}:tenant-charge:{chargeLedgerEntryId}";
}
