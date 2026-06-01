using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Assesses late fees on overdue rent payments.
/// <para>
/// Each cycle: loads rent payments past the grace cutoff, checks idempotency via a DB-level
/// unique index on (LeaseId, PaymentType, PeriodKey), applies per-state caps, creates a
/// <see cref="PaymentType.LateFee"/> row, and flips the source rent payment to
/// <see cref="PaymentStatus.Late"/>. Optionally publishes a tenant notification via the outbox.
/// </para>
/// </summary>
public sealed class LateFeeService : ILateFeeService
{
    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly NotificationsConfig _cfg;
    private readonly ILogger<LateFeeService> _logger;

    public LateFeeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        IOptions<NotificationsConfig> options,
        ILogger<LateFeeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _cfg = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> AssessAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableLateFees)
            return 0;

        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(-_cfg.LateFeeGraceDays);

        // Load all overdue rent payments that are still unpaid / partially paid / already late.
        var overdueRent = await _db.Payments
            .Where(p =>
                p.PaymentType == PaymentType.Rent &&
                p.PeriodKey != null &&   // only auto-generated rent charges carry a period; manual entries are excluded
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Late ||
                 p.Status == PaymentStatus.Partial) &&
                p.DueDate < cutoff)
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Property)
            .ToListAsync(ct);

        if (overdueRent.Count == 0)
            return 0;

        _logger.LogDebug("LateFeeService found {Count} overdue rent payment(s) past grace cutoff {Cutoff:yyyy-MM-dd}",
            overdueRent.Count, cutoff);

        var now = DateTime.UtcNow;
        var count = 0;

        foreach (var rp in overdueRent)
        {
            ct.ThrowIfCancellationRequested();

            if (rp.Lease is null)
            {
                _logger.LogWarning("Payment {PaymentId} has no Lease navigation; skipping", rp.Id);
                continue;
            }

            // Billing-period key from the auto-generated rent row (the query excludes manual rows,
            // so PeriodKey is always set here — keeps late fees paired 1:1 with the rent charge).
            var periodKey = rp.PeriodKey!;

            // ---- Idempotency check (application-level; DB unique index is the backstop) ----
            var alreadyAssessed = await _db.Payments.AnyAsync(
                p => p.LeaseId == rp.LeaseId &&
                     p.PaymentType == PaymentType.LateFee &&
                     p.PeriodKey == periodKey,
                ct);

            if (alreadyAssessed)
                continue;

            // ---- Compute fee amount ----
            var fee = rp.Lease.LateFeeAmount;
            if (fee <= 0)
            {
                _logger.LogDebug("Lease {LeaseId} has LateFeeAmount <= 0; skipping period {Period}",
                    rp.LeaseId, periodKey);
                continue;
            }

            // Apply state cap (flat and/or percent-of-rent) if configured.
            var state = rp.Lease.Property?.State ?? string.Empty;
            if (_cfg.StateLateFeeCaps.TryGetValue(state, out var cap))
            {
                // Flat-dollar cap: fee may not exceed MaxFlat.
                if (cap.MaxFlat is decimal mf)
                    fee = Math.Min(fee, mf);

                // Percent-of-rent cap: fee may not exceed (MonthlyRent * MaxPercentOfRent / 100).
                if (cap.MaxPercentOfRent is decimal mp)
                    fee = Math.Min(fee, rp.Lease.MonthlyRent * mp / 100m);
            }

            fee = Math.Round(fee, 2);
            if (fee <= 0)
            {
                _logger.LogDebug("Lease {LeaseId} late fee reduced to zero by state cap ({State}); skipping period {Period}",
                    rp.LeaseId, state, periodKey);
                continue;
            }

            // ---- Create the late-fee payment row ----
            var lateFeePayment = new Payment
            {
                PortfolioId  = rp.PortfolioId,
                LeaseId      = rp.LeaseId,
                PaymentType  = PaymentType.LateFee,
                Status       = PaymentStatus.Scheduled,
                Amount       = fee,
                DueDate      = today,
                PeriodKey    = periodKey,
                CreatedAt    = now,
                UpdatedAt    = now,
            };
            _db.Payments.Add(lateFeePayment);

            // ---- Mark the source rent payment Late ----
            if (rp.Status != PaymentStatus.Late)
            {
                rp.Status    = PaymentStatus.Late;
                rp.UpdatedAt = now;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                count++;

                _logger.LogInformation(
                    "Late fee ${Fee} assessed for lease {LeaseId} period {Period} (rent payment {RentPaymentId})",
                    fee, rp.LeaseId, periodKey, rp.Id);
            }
            catch (DbUpdateException ex)
            {
                // The DB unique index (LeaseId, PaymentType, PeriodKey) fired — another process
                // raced us or the app-level check above had a bug. Detach and continue.
                _logger.LogWarning(ex,
                    "DbUpdateException (likely duplicate) for lease {LeaseId} period {Period}; skipping",
                    rp.LeaseId, periodKey);

                // Detach the unsaved fee and discard the in-memory rent-status change — the failed
                // save rolled back, so reload restores rp to its persisted state.
                _db.Entry(lateFeePayment).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                await _db.Entry(rp).ReloadAsync(ct);
                continue;
            }

            // ---- Optional tenant notification ----
            if (!_cfg.NotifyTenants)
                continue;

            try
            {
                // Fetch the tenant's contact details for this lease.
                // FirstOrDefaultAsync respects the soft-delete global query filter;
                // FindAsync bypasses it and would return deleted tenants.
                var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == rp.Lease.TenantId, ct);
                if (tenant is null)
                    continue;

                if (!string.IsNullOrWhiteSpace(tenant.Email))
                {
                    await _publisher.PublishAsync(
                        rp.PortfolioId,
                        "email",
                        new
                        {
                            to      = tenant.Email,
                            subject = $"Late fee notice — {periodKey}",
                            body    = $"A late fee of ${fee:F2} has been assessed on your account for the {periodKey} billing period. " +
                                      "Please contact your property manager if you have questions.",
                        },
                        ct);
                }
                else if (!string.IsNullOrWhiteSpace(tenant.Phone))
                {
                    await _publisher.PublishAsync(
                        rp.PortfolioId,
                        "sms",
                        new
                        {
                            to      = tenant.Phone,
                            message = $"A late fee of ${fee:F2} has been assessed for {periodKey}. Contact your manager with questions.",
                        },
                        ct);
                }
            }
            catch (Exception ex)
            {
                // Notification failure is non-fatal; the fee row is already persisted.
                _logger.LogWarning(ex,
                    "Failed to enqueue tenant notification for lease {LeaseId} period {Period}",
                    rp.LeaseId, periodKey);
            }
        }

        return count;
    }
}
