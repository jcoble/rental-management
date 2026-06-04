using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
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
    private readonly ILogger<StripePaymentService> _logger;

    public StripePaymentService(
        RentalCommandDbContext db,
        IOptions<StripeConfig> config,
        ILogger<StripePaymentService> logger)
    {
        _db = db;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<CreateIntentResult> CreatePaymentIntentAsync(int portfolioId, int paymentId, CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — create-intent is a no-op");
            return CreateIntentResult.NotEnabled();
        }

        var payment = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.PortfolioId == portfolioId, ct);

        if (payment == null)
        {
            return CreateIntentResult.NotFound();
        }

        var requestOptions = new RequestOptions { ApiKey = _config.SecretKey };
        var intentService = new PaymentIntentService();
        var intent = await intentService.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)(payment.Amount * 100),
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

        var now = DateTime.UtcNow;
        var transaction = new PaymentTransaction
        {
            PortfolioId = portfolioId,
            PaymentId = paymentId,
            Amount = payment.Amount,
            Currency = "usd",
            Provider = "stripe",
            ProviderPaymentIntentId = intent.Id,
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created Stripe PaymentIntent {IntentId} for payment {PaymentId} (portfolio {PortfolioId})",
            intent.Id, paymentId, portfolioId);

        return CreateIntentResult.Ok(intent.ClientSecret!, _config.PublishableKey, transaction.Id);
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

        // OWNERSHIP: the payment must be in this portfolio AND on a lease belonging to the calling
        // tenant. A payment on someone else's lease is simply "not found" — a tenant can never pay,
        // or even probe the existence of, another tenant's rent (IDOR guard).
        var payment = await _db.Payments
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

        var requestOptions = new RequestOptions { ApiKey = _config.SecretKey };
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
                        UnitAmount = (long)(payment.Amount * 100),
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

        // Record a pending transaction now; the webhook flips it (and the Payment) on completion.
        var now = DateTime.UtcNow;
        var transaction = new PaymentTransaction
        {
            PortfolioId = portfolioId,
            PaymentId = paymentId,
            Amount = payment.Amount,
            Currency = "usd",
            Provider = "stripe",
            ProviderPaymentIntentId = session.Id, // Checkout session id; reconciled on completion.
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

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

        // Idempotency check — if we have already processed this event, skip it.
        var existing = await _db.StripeWebhookEvents
            .FirstOrDefaultAsync(e => e.EventId == ev.Id, ct);
        if (existing != null)
        {
            _logger.LogInformation("Stripe webhook event {EventId} already processed — skipping", ev.Id);
            return;
        }

        var now = DateTime.UtcNow;
        var webhookEvent = new StripeWebhookEvent
        {
            EventId = ev.Id,
            EventType = ev.Type,
            ReceivedAt = now
        };
        _db.StripeWebhookEvents.Add(webhookEvent);

        // Run the per-type handler FIRST. Only if it succeeds do we mark the event processed and
        // commit. If the handler throws, the exception propagates (→ controller returns non-2xx) and
        // nothing is saved, so Stripe retries and we never leave a phantom "processed" event behind
        // with stale payment state.
        {
            switch (ev.Type)
            {
                case "payment_intent.succeeded":
                {
                    var intent = ev.Data.Object as PaymentIntent;
                    if (intent != null)
                    {
                        var transaction = await _db.PaymentTransactions
                            .Include(t => t.Payment)
                            .FirstOrDefaultAsync(t => t.ProviderPaymentIntentId == intent.Id, ct);

                        if (transaction != null)
                        {
                            transaction.Status = PaymentTransactionStatus.Succeeded;
                            transaction.UpdatedAt = now;

                            if (transaction.Payment != null)
                            {
                                transaction.Payment.Status = PaymentStatus.Paid;
                                transaction.Payment.PaidDate = now;
                                transaction.Payment.Method = "card";
                                transaction.Payment.ExternalReference = intent.Id;
                                transaction.Payment.UpdatedAt = now;
                            }

                            _logger.LogInformation(
                                "Stripe PaymentIntent {IntentId} succeeded — payment {PaymentId} marked Paid",
                                intent.Id, transaction.PaymentId);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "No PaymentTransaction found for succeeded PaymentIntent {IntentId}", intent.Id);
                        }
                    }
                    break;
                }

                case "payment_intent.payment_failed":
                {
                    var intent = ev.Data.Object as PaymentIntent;
                    if (intent != null)
                    {
                        var transaction = await _db.PaymentTransactions
                            .FirstOrDefaultAsync(t => t.ProviderPaymentIntentId == intent.Id, ct);

                        if (transaction != null)
                        {
                            transaction.Status = PaymentTransactionStatus.Failed;
                            transaction.FailureReason = intent.LastPaymentError?.Message;
                            transaction.UpdatedAt = now;

                            _logger.LogInformation(
                                "Stripe PaymentIntent {IntentId} failed — reason: {Reason}",
                                intent.Id, transaction.FailureReason);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "No PaymentTransaction found for failed PaymentIntent {IntentId}", intent.Id);
                        }
                    }
                    break;
                }

                case "checkout.session.completed":
                {
                    var session = ev.Data.Object as Session;
                    if (session != null)
                    {
                        await HandleCheckoutSessionCompletedAsync(session, now, ct);
                    }
                    break;
                }

                default:
                    // Unknown event type — safe no-op; still recorded and returns 200.
                    _logger.LogDebug("Stripe webhook event type {EventType} received but not handled", ev.Type);
                    break;
            }
        }

        // Handler completed without throwing — record the event as processed and persist the
        // payment/transaction mutations together in a single SaveChanges.
        webhookEvent.ProcessedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Dispatches a completed Checkout session: a setup session (mode=setup, autopay metadata) stores
    /// an <see cref="AutopayEnrollment"/>; a payment session marks the linked <c>Payment</c> Paid once
    /// Stripe reports the session paid. ACH sessions may complete before settlement — only flip to Paid
    /// when PaymentStatus == "paid"; the PaymentIntent webhook also covers the later success.
    /// </summary>
    private async Task HandleCheckoutSessionCompletedAsync(Session session, DateTime now, CancellationToken ct)
    {
        var metadata = session.Metadata ?? new Dictionary<string, string>();

        // --- Autopay setup session ---
        if (session.Mode == "setup" ||
            (metadata.TryGetValue(MetadataAutopay, out var autopayFlag) && autopayFlag == "1"))
        {
            await HandleAutopaySetupCompletedAsync(session, metadata, now, ct);
            return;
        }

        // --- One-off rent payment session ---
        // Only mark Paid once Stripe confirms the session itself is paid (card success; ACH that
        // already settled). For still-processing ACH, leave the transaction Pending — the
        // payment_intent.succeeded webhook flips it later.
        if (!string.Equals(session.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Checkout session {SessionId} completed but not yet paid (status {Status}) — leaving transaction pending",
                session.Id, session.PaymentStatus);
            return;
        }

        var transaction = await _db.PaymentTransactions
            .Include(t => t.Payment)
            .FirstOrDefaultAsync(t => t.ProviderPaymentIntentId == session.Id, ct);

        if (transaction == null)
        {
            _logger.LogWarning("No PaymentTransaction found for completed Checkout session {SessionId}", session.Id);
            return;
        }

        transaction.Status = PaymentTransactionStatus.Succeeded;
        transaction.UpdatedAt = now;
        // Pin the real PaymentIntent id so a later payment_intent.succeeded event reconciles to the
        // same transaction (idempotent — it finds this row by ProviderPaymentIntentId).
        if (!string.IsNullOrEmpty(session.PaymentIntentId))
        {
            transaction.ProviderPaymentIntentId = session.PaymentIntentId;
        }

        if (transaction.Payment != null && transaction.Payment.Status != PaymentStatus.Paid)
        {
            transaction.Payment.Status = PaymentStatus.Paid;
            transaction.Payment.PaidDate = now;
            transaction.Payment.Method = "online";
            transaction.Payment.ExternalReference = session.PaymentIntentId ?? session.Id;
            transaction.Payment.UpdatedAt = now;
        }

        _logger.LogInformation(
            "Checkout session {SessionId} paid — payment {PaymentId} marked Paid",
            session.Id, transaction.PaymentId);
    }

    /// <summary>
    /// Resolves the saved payment method from the session's SetupIntent and stores (or refreshes) the
    /// tenant's <see cref="AutopayEnrollment"/> as Active. Idempotent: a duplicate webhook delivery
    /// updates the same active enrollment rather than creating a second.
    /// </summary>
    private async Task HandleAutopaySetupCompletedAsync(
        Session session, IDictionary<string, string> metadata, DateTime now, CancellationToken ct)
    {
        if (!metadata.TryGetValue(MetadataLeaseId, out var leaseRaw) || !int.TryParse(leaseRaw, out var leaseId) ||
            !metadata.TryGetValue(MetadataTenantId, out var tenantRaw) || !int.TryParse(tenantRaw, out var tenantId) ||
            !metadata.TryGetValue(MetadataPortfolioId, out var portfolioRaw) || !int.TryParse(portfolioRaw, out var portfolioId))
        {
            _logger.LogWarning(
                "Autopay setup session {SessionId} completed without resolvable lease/tenant/portfolio metadata — skipping",
                session.Id);
            return;
        }

        // Resolve the saved payment method. The session carries the SetupIntent id; retrieve it to
        // read the saved PaymentMethod + Customer that Stripe created for us.
        string? customerId = session.CustomerId;
        string? paymentMethodId = null;
        if (!string.IsNullOrEmpty(session.SetupIntentId))
        {
            var setupIntentService = new SetupIntentService();
            var setupIntent = await setupIntentService.GetAsync(
                session.SetupIntentId,
                requestOptions: new RequestOptions { ApiKey = _config.SecretKey },
                cancellationToken: ct);
            paymentMethodId = setupIntent.PaymentMethodId;
            customerId ??= setupIntent.CustomerId;
        }

        if (string.IsNullOrEmpty(paymentMethodId) || string.IsNullOrEmpty(customerId))
        {
            _logger.LogWarning(
                "Autopay setup session {SessionId} completed but no saved payment method/customer was resolved — not enrolling",
                session.Id);
            return;
        }

        var enrollment = await _db.AutopayEnrollments
            .FirstOrDefaultAsync(e => e.LeaseId == leaseId && e.Active, ct);

        if (enrollment == null)
        {
            enrollment = new AutopayEnrollment
            {
                PortfolioId = portfolioId,
                LeaseId = leaseId,
                TenantId = tenantId,
                CreatedAt = now,
            };
            _db.AutopayEnrollments.Add(enrollment);
        }

        enrollment.StripeCustomerId = customerId;
        enrollment.StripePaymentMethodId = paymentMethodId;
        enrollment.Active = true;
        enrollment.UpdatedAt = now;

        _logger.LogInformation(
            "Autopay enrolled for lease {LeaseId} (tenant {TenantId}, portfolio {PortfolioId}) via session {SessionId}",
            leaseId, tenantId, portfolioId, session.Id);
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
}
