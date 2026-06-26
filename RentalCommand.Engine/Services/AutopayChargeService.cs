using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <inheritdoc cref="IAutopayChargeService"/>
public sealed class AutopayChargeService : IAutopayChargeService
{
    private readonly RentalCommandDbContext _db;
    private readonly StripeConfig _config;
    private readonly ILogger<AutopayChargeService> _logger;

    public AutopayChargeService(
        RentalCommandDbContext db,
        IOptions<StripeConfig> config,
        ILogger<AutopayChargeService> logger)
    {
        _db = db;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> ChargeDueAsync(CancellationToken ct = default)
    {
        // GATE: do nothing at all unless Stripe is configured. This keeps autopay a true no-op in
        // builds/environments without keys (never a false charge, never a thrown error).
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — autopay charging is a no-op");
            return 0;
        }

        var now = DateTime.UtcNow;

        // Candidate payments: scheduled (or late) rent, due now, on a lease with an ACTIVE autopay
        // enrollment. Joining through the active enrollment is the selection gate — leases that
        // cancelled (Active=false) or never enrolled are excluded.
        var candidates = await (
            from p in _db.Payments.ForCurrentLeaseAttention(now)
            where (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late)
                  && p.PaymentType == PaymentType.Rent
                  && p.DueDate <= now
            join e in _db.AutopayEnrollments.Where(e => e.Active)
                on p.LeaseId equals e.LeaseId
            select new { Payment = p, Enrollment = e })
            .ToListAsync(ct);

        var charged = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var payment = candidate.Payment;
            var enrollment = candidate.Enrollment;

            if (string.IsNullOrEmpty(enrollment.StripeCustomerId) ||
                string.IsNullOrEmpty(enrollment.StripePaymentMethodId))
            {
                _logger.LogWarning(
                    "Autopay enrollment {EnrollmentId} (lease {LeaseId}) has no saved customer/payment method — skipping",
                    enrollment.Id, enrollment.LeaseId);
                continue;
            }

            // Deterministic per-charge key derived from the payment + its billing period. The SAME
            // key is (a) stored on the local PaymentTransaction so a crashed cycle can recover its
            // row, and (b) sent to Stripe as the IdempotencyKey so a retry returns the ORIGINAL
            // PaymentIntent instead of charging the tenant a second time.
            var idempotencyKey = BuildIdempotencyKey(payment);

            // IDEMPOTENCY (per-payment): never double-charge. Skip if this payment already has a
            // transaction that is pending or already succeeded. A previously Failed transaction does
            // NOT block a retry.
            var alreadyInFlight = await _db.PaymentTransactions.AnyAsync(
                t => t.PaymentId == payment.Id
                     && (t.Status == PaymentTransactionStatus.Pending || t.Status == PaymentTransactionStatus.Succeeded),
                ct);
            if (alreadyInFlight)
            {
                continue;
            }

            // CRASH RECOVERY (per-key): a prior cycle may have created a transaction for this exact
            // charge and then died before the in-flight check above could see a fresh-context row.
            // If a non-failed row with this key already exists, do NOT charge again — the Stripe
            // idempotency key already protected the tenant and the webhook/reconcile will finish it.
            var priorAttempt = await _db.PaymentTransactions
                .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);
            if (priorAttempt is not null && priorAttempt.Status != PaymentTransactionStatus.Failed)
            {
                continue;
            }

            // CRASH-SAFE ORDERING: persist (or reuse) a Pending transaction row carrying the
            // idempotency key BEFORE calling Stripe, so a row that can dedupe the charge always exists
            // even if the process dies in the window between the charge succeeding and us recording it.
            var transaction = priorAttempt ?? new PaymentTransaction
            {
                PortfolioId = payment.PortfolioId,
                PaymentId = payment.Id,
                Amount = payment.Amount,
                Currency = "usd",
                Provider = "stripe",
                IdempotencyKey = idempotencyKey,
                CreatedAt = now,
            };
            if (priorAttempt is null)
            {
                _db.PaymentTransactions.Add(transaction);
            }
            transaction.Status = PaymentTransactionStatus.Pending;
            transaction.FailureReason = null;
            transaction.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            try
            {
                var requestOptions = new RequestOptions
                {
                    ApiKey = _config.SecretKey,
                    // Deterministic: a retry of this exact charge returns the original PaymentIntent
                    // rather than minting a new one — Stripe never debits the tenant twice.
                    IdempotencyKey = idempotencyKey,
                };
                var intentService = new PaymentIntentService();
                var intent = await intentService.CreateAsync(new PaymentIntentCreateOptions
                {
                    Amount = (long)(payment.Amount * 100),
                    Currency = "usd",
                    Customer = enrollment.StripeCustomerId,
                    PaymentMethod = enrollment.StripePaymentMethodId,
                    // Confirm immediately, off-session: the tenant isn't present (background charge).
                    Confirm = true,
                    OffSession = true,
                    Metadata = new Dictionary<string, string>
                    {
                        ["paymentId"] = payment.Id.ToString(),
                        ["portfolioId"] = payment.PortfolioId.ToString(),
                        ["autopay"] = "1",
                    },
                }, requestOptions, ct);

                // Pin the PaymentIntent id onto the pre-created row so the API's existing
                // payment_intent.succeeded webhook reconciles it and marks the Payment Paid.
                transaction.ProviderPaymentIntentId = intent.Id;
                transaction.Status = MapStatus(intent.Status);
                transaction.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                charged++;

                _logger.LogInformation(
                    "Autopay charged payment {PaymentId} (lease {LeaseId}) off-session via PaymentIntent {IntentId} (status {Status})",
                    payment.Id, payment.LeaseId, intent.Id, intent.Status);
            }
            catch (StripeException ex)
            {
                // A declined/errored off-session charge is logged — the payment stays scheduled so the
                // landlord can follow up / the tenant can pay manually. Mark the pre-created row Failed
                // (we have a row now) so it neither blocks the per-payment in-flight guard nor the
                // per-key recovery guard on the next run, and we don't crash the cycle.
                transaction.Status = PaymentTransactionStatus.Failed;
                transaction.FailureReason = ex.Message;
                transaction.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                _logger.LogWarning(
                    ex,
                    "Autopay off-session charge failed for payment {PaymentId} (lease {LeaseId}); leaving scheduled",
                    payment.Id, payment.LeaseId);
            }
        }

        if (charged > 0)
        {
            _logger.LogInformation("AutopayChargeService initiated {Count} off-session charge(s).", charged);
        }

        return charged;
    }

    /// <summary>
    /// Deterministic idempotency key for one autopay charge: a retry of the SAME due payment produces
    /// the SAME key, so Stripe returns the original PaymentIntent instead of charging twice. Keyed by
    /// payment id + billing period; falls back to the due date when a payment has no PeriodKey so the
    /// key is still stable and unique per scheduled charge.
    /// </summary>
    private static string BuildIdempotencyKey(Payment payment)
    {
        var period = string.IsNullOrEmpty(payment.PeriodKey)
            ? payment.DueDate.ToString("yyyyMMdd")
            : payment.PeriodKey;
        return $"autopay-{payment.Id}-{period}";
    }

    private static PaymentTransactionStatus MapStatus(string? stripeStatus) => stripeStatus switch
    {
        "succeeded" => PaymentTransactionStatus.Succeeded,
        "canceled" => PaymentTransactionStatus.Canceled,
        _ => PaymentTransactionStatus.Pending,
    };
}
