using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private const string DefaultTimeZoneId = "America/New_York";

    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly NotificationsConfig _cfg;
    private readonly TimeZoneInfo _businessTimeZone;
    private readonly ILogger<LateFeeService> _logger;

    public LateFeeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        IOptions<NotificationsConfig> options,
        IConfiguration configuration,
        ILogger<LateFeeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _cfg = options.Value;
        _logger = logger;

        // Whether rent is "past due" — and by how many days — rolls over in the landlord's LOCAL
        // zone, not UTC. Near month-end an evening (ET) UtcNow is already the next day in UTC, which
        // would mis-date the grace cutoff. Derive the business "today"/cutoff in this zone; DB writes
        // stay UTC.
        var tzId = configuration["App:TimeZone"];
        if (string.IsNullOrWhiteSpace(tzId))
            tzId = DefaultTimeZoneId;
        _businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    /// <inheritdoc />
    public async Task<int> AssessAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableLateFees)
            return 0;

        // DA#4: derive the business "today" from the landlord's LOCAL zone so the grace cutoff and
        // "past due" decision use their calendar day, not UTC's. Rent DueDates are stored as UTC
        // midnight, so express today/cutoff as UTC-midnight too (Kind=Utc) to compare like-for-like
        // and to keep the late-fee row's DueDate a UTC value.
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTimeZone).Date;
        var today = new DateTime(localToday.Year, localToday.Month, localToday.Day, 0, 0, 0, DateTimeKind.Utc);
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

            // M15: the late-fee row, the source rent's status flip, and the tenant notification must
            // commit atomically — separate saves could leave a fee without its notice (or a notice
            // without the fee). Wrap them in one EF transaction; the publisher shares this DbContext,
            // so its outbox insert enlists here too. A rollback lets the next cycle retry both
            // together (the idempotency check above prevents duplicates).
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _db.SaveChangesAsync(ct);

                // ---- Optional tenant notification (enqueued inside the same transaction) ----
                if (_cfg.NotifyTenants)
                {
                    // Fetch the tenant's contact details for this lease.
                    // FirstOrDefaultAsync respects the soft-delete global query filter;
                    // FindAsync bypasses it and would return deleted tenants.
                    var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == rp.Lease.TenantId, ct);

                    if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.Email))
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
                    else if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.Phone))
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

                await tx.CommitAsync(ct);
                count++;

                _logger.LogInformation(
                    "Late fee ${Fee} assessed for lease {LeaseId} period {Period} (rent payment {RentPaymentId})",
                    fee, rp.LeaseId, periodKey, rp.Id);
            }
            catch (DbUpdateException ex)
            {
                // The DB unique index (LeaseId, PaymentType, PeriodKey) fired — another process
                // raced us or the app-level check above had a bug. Roll back, detach, and continue.
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex,
                    "DbUpdateException (likely duplicate) for lease {LeaseId} period {Period}; skipping",
                    rp.LeaseId, periodKey);

                // Detach the unsaved fee and discard the in-memory rent-status change — the rollback
                // undid the DB write, so reload restores rp to its persisted state.
                _db.Entry(lateFeePayment).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                await _db.Entry(rp).ReloadAsync(ct);
                continue;
            }
            catch (Exception ex)
            {
                // Any other failure (e.g. notification enqueue) rolls back the whole unit so we never
                // persist a fee without its notice. The next cycle retries both atomically.
                await tx.RollbackAsync(ct);
                _logger.LogWarning(ex,
                    "Failed to assess late fee + notice for lease {LeaseId} period {Period}; rolled back, will retry",
                    rp.LeaseId, periodKey);

                _db.Entry(lateFeePayment).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                await _db.Entry(rp).ReloadAsync(ct);
                continue;
            }
        }

        return count;
    }
}
