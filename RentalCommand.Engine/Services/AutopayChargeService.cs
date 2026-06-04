using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
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
            from p in _db.Payments
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

            // IDEMPOTENCY: never double-charge. Skip if this payment already has a transaction that is
            // pending or already succeeded. A previously Failed transaction does NOT block a retry.
            var alreadyInFlight = await _db.PaymentTransactions.AnyAsync(
                t => t.PaymentId == payment.Id
                     && (t.Status == PaymentTransactionStatus.Pending || t.Status == PaymentTransactionStatus.Succeeded),
                ct);
            if (alreadyInFlight)
            {
                continue;
            }

            try
            {
                var requestOptions = new RequestOptions { ApiKey = _config.SecretKey };
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

                // Record the pending transaction keyed by the PaymentIntent id so the API's existing
                // payment_intent.succeeded webhook reconciles it and marks the Payment Paid.
                _db.PaymentTransactions.Add(new PaymentTransaction
                {
                    PortfolioId = payment.PortfolioId,
                    PaymentId = payment.Id,
                    Amount = payment.Amount,
                    Currency = "usd",
                    Provider = "stripe",
                    ProviderPaymentIntentId = intent.Id,
                    Status = MapStatus(intent.Status),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await _db.SaveChangesAsync(ct);
                charged++;

                _logger.LogInformation(
                    "Autopay charged payment {PaymentId} (lease {LeaseId}) off-session via PaymentIntent {IntentId} (status {Status})",
                    payment.Id, payment.LeaseId, intent.Id, intent.Status);
            }
            catch (StripeException ex)
            {
                // A declined/errored off-session charge is logged and skipped — the payment stays
                // scheduled so the landlord can follow up / the tenant can pay manually. We don't
                // persist a failed transaction here (no PaymentIntent id to key on) and we don't crash
                // the cycle; the next run re-attempts (guarded by the in-flight idempotency check).
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

    private static PaymentTransactionStatus MapStatus(string? stripeStatus) => stripeStatus switch
    {
        "succeeded" => PaymentTransactionStatus.Succeeded,
        "canceled" => PaymentTransactionStatus.Canceled,
        _ => PaymentTransactionStatus.Pending,
    };
}
