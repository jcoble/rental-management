using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
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
    private readonly INotificationSettingsService _settings;
    private readonly IDataUpdateService _dataUpdate;
    private readonly NotificationsConfig _defaults;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly ILogger<LateFeeService> _logger;

    public LateFeeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        INotificationSettingsService settings,
        IDataUpdateService dataUpdate,
        IOptions<NotificationsConfig> options,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        ILogger<LateFeeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _dataUpdate = dataUpdate;
        _defaults = options.Value;
        _timeProvider = timeProvider;
        _tz = tz;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> AssessAsync(CancellationToken ct = default)
    {
        // DA#4: derive the business "today" from the landlord's LOCAL zone so the grace cutoff and
        // "past due" decision use their calendar day, not UTC's. Rent DueDates are stored as UTC
        // midnight, so express today/cutoff as UTC-midnight too (Kind=Utc) to compare like-for-like
        // and to keep the late-fee row's DueDate a UTC value.
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), _tz.BusinessTimeZone).Date;
        var today = new DateTime(localToday.Year, localToday.Month, localToday.Day, 0, 0, 0, DateTimeKind.Utc);

        // Grace days are per-portfolio now, so the cutoff is too. Load overdue rent broadly and gate
        // each payment against its own portfolio's cutoff + master flag below.
        var overdueRent = await _db.Payments
            .ForCurrentLeaseAttention(today)
            .Where(p =>
                p.PaymentType == PaymentType.Rent &&
                p.PeriodKey != null &&   // only auto-generated rent charges carry a period; manual entries are excluded
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Late ||
                 p.Status == PaymentStatus.Partial) &&
                p.DueDate < today)       // can't be past any grace window if not yet past due
            .Include(p => p.Lease)
                .ThenInclude(l => l!.Property)
            .ToListAsync(ct);

        if (overdueRent.Count == 0)
            return 0;

        // Idempotency set, loaded ONCE for the candidate leases instead of an AnyAsync per overdue
        // payment (N+1). A LateFee already exists for a (lease, period) pair when a prior cycle created
        // it; we check this set in memory and add to it after each successful commit below. The DB
        // unique index (LeaseId, PaymentType, PeriodKey) remains the hard backstop against a race.
        var candidateLeaseIds = overdueRent.Select(p => p.LeaseId).Distinct().ToList();
        var existingLateFeeKeys = (await _db.Payments
                .AsNoTracking()
                .Where(p => p.PaymentType == PaymentType.LateFee &&
                            p.PeriodKey != null &&
                            candidateLeaseIds.Contains(p.LeaseId))
                .Select(p => new { p.LeaseId, p.PeriodKey })
                .ToListAsync(ct))
            .Select(p => (p.LeaseId, p.PeriodKey!))
            .ToHashSet();

        var notifier = new AutomationNotifier(_db, _publisher);
        var configCache = new Dictionary<int, NotificationsConfig>();
        var now = _timeProvider.UtcNow();
        var count = 0;

        foreach (var rp in overdueRent)
        {
            ct.ThrowIfCancellationRequested();

            if (rp.Lease is null)
            {
                _logger.LogWarning("Payment {PaymentId} has no Lease navigation; skipping", rp.Id);
                continue;
            }

            // Resolve (and memoize) this payment's portfolio config; apply the global state caps.
            if (!configCache.TryGetValue(rp.PortfolioId, out var cfg))
            {
                cfg = await _settings.GetRuntimeAsync(rp.PortfolioId, ct);
                cfg.StateLateFeeCaps = _defaults.StateLateFeeCaps;
                configCache[rp.PortfolioId] = cfg;
            }

            if (!cfg.EnableLateFees)
                continue;

            // Per-portfolio grace cutoff: only assess once the payment is past this portfolio's window.
            if (rp.DueDate >= today.AddDays(-cfg.LateFeeGraceDays))
                continue;

            // Billing-period key from the auto-generated rent row (the query excludes manual rows,
            // so PeriodKey is always set here — keeps late fees paired 1:1 with the rent charge).
            var periodKey = rp.PeriodKey!;

            // ---- Idempotency check (application-level; DB unique index is the backstop) ----
            // In-memory lookup against the pre-loaded set (no per-row query).
            if (existingLateFeeKeys.Contains((rp.LeaseId, periodKey)))
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
            if (cfg.StateLateFeeCaps.TryGetValue(state, out var cap))
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
            var inAppRows = (IReadOnlyList<Notification>)Array.Empty<Notification>();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _db.SaveChangesAsync(ct);

                // ---- Notification on the channels enabled for LateFee in this portfolio ----
                if (cfg.NotifyTenants)
                {
                    // Fetch the tenant's contact details for this lease.
                    // FirstOrDefaultAsync respects the soft-delete global query filter;
                    // FindAsync bypasses it and would return deleted tenants.
                    var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == rp.Lease.TenantId, ct);

                    var channels = cfg.ResolveChannels(NotificationType.LateFee);
                    var emailBody = $"A late fee of ${fee:F2} has been assessed on your account for the {periodKey} billing period. "
                                    + "Please contact your property manager if you have questions.";
                    var smsBody = $"A late fee of ${fee:F2} has been assessed for {periodKey}. Contact your manager with questions.";
                    var tenantMessage = $"A late fee of ${fee:F2} has been assessed for {periodKey}. Contact your manager with questions.";

                    inAppRows = await notifier.SendAsync(
                        rp.PortfolioId,
                        channels,
                        new AutomationNotifier.InAppContent(
                            Type: "LateFee",
                            Title: $"Late fee assessed — {periodKey}",
                            Message: $"A late fee of ${fee:F2} was assessed on lease {rp.Lease.LeaseNumber} for {periodKey}.",
                            Severity: "Warning",
                            ActionUrl: $"/payments/{lateFeePayment.Id}",
                            RelatedEntityType: "Payment",
                            RelatedEntityId: lateFeePayment.Id),
                        new AutomationNotifier.EmailContent(tenant?.Email, $"Late fee notice — {periodKey}", emailBody),
                        new AutomationNotifier.SmsContent(tenant?.Phone, smsBody),
                        now,
                        ct,
                        tenant is null
                            ? new AutomationNotifier.AudienceTargets(IncludeStaff: true)
                            : new AutomationNotifier.AudienceTargets(
                                IncludeStaff: true,
                                TenantId: tenant.Id,
                                TenantInApp: new AutomationNotifier.InAppContent(
                                    Type: "LateFee",
                                    Title: $"Late fee assessed — {periodKey}",
                                    Message: tenantMessage,
                                    Severity: "Warning",
                                    ActionUrl: "/portal/payments",
                                    RelatedEntityType: "Payment",
                                    RelatedEntityId: lateFeePayment.Id)));

                    if (inAppRows.Count > 0)
                        await _db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
                count++;
                // Record the just-assessed (lease, period) so a duplicate in this same batch is skipped.
                existingLateFeeKeys.Add((rp.LeaseId, periodKey));

                foreach (var row in inAppRows)
                    await _dataUpdate.BroadcastEntityUpdateAsync(
                        rp.PortfolioId, "Notification", row.Id, NotificationResponse.FromEntity(row), ct);

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
