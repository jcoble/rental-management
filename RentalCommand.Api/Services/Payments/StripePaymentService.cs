using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Payments;

/// <inheritdoc cref="IStripePaymentService"/>
public class StripePaymentService : IStripePaymentService
{
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
    public async Task HandleWebhookEventAsync(string json, string signature, CancellationToken ct)
    {
        // Throws StripeException on bad signature — let it propagate to the controller (→ 400).
        var ev = EventUtility.ConstructEvent(json, signature, _config.WebhookSecret);

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

        try
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

                default:
                    // Unknown event type — safe no-op; still recorded and returns 200.
                    _logger.LogDebug("Stripe webhook event type {EventType} received but not handled", ev.Type);
                    break;
            }
        }
        finally
        {
            // Always mark processed and save, even if the per-type handler encountered an error.
            webhookEvent.ProcessedAt = now;
            await _db.SaveChangesAsync(ct);
        }
    }
}
