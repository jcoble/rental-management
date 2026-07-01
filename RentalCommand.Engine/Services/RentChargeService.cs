using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// Generates one <c>Scheduled</c> rent <see cref="Payment"/> per active lease per billing period.
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>).
/// </summary>
public sealed class RentChargeService : IRentChargeService
{
    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly INotificationSettingsService _settings;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly ILogger<RentChargeService> _logger;

    private readonly record struct RentChargeCandidate(
        Lease Lease,
        RentChargePeriod Period,
        NotificationsConfig Config);

    public RentChargeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        INotificationSettingsService settings,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        ILogger<RentChargeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _tz = tz;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Business "today" in the landlord's local zone (drives period key + due-day math only). Near
        // month-end an evening (ET) UtcNow is already the next day/month in UTC, which would charge rent
        // for the wrong period. Every value WRITTEN to the DB below stays UTC (Kind=Utc).
        var today = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), _tz.BusinessTimeZone).Date;

        var leases = await _db.Leases
            .Where(l => l.Status == LeaseStatus.Active)
            .Include(l => l.Tenant)
            .ToListAsync(ct);

        var configCache = new Dictionary<int, NotificationsConfig>();
        var candidates = new List<RentChargeCandidate>();

        foreach (var lease in leases)
        {
            ct.ThrowIfCancellationRequested();

            if (!configCache.TryGetValue(lease.PortfolioId, out var cfg))
            {
                cfg = await _settings.GetRuntimeAsync(lease.PortfolioId, ct);
                configCache[lease.PortfolioId] = cfg;
            }

            if (!cfg.EnableRentCharges || lease.MonthlyRent <= 0)
                continue;

            var periods = RentChargeSchedule.GetDuePeriods(
                RentChargeGenerationStart(lease),
                lease.EndDate,
                lease.RentDueDay,
                today,
                cfg.RentChargeLeadDays);

            foreach (var period in periods)
            {
                candidates.Add(new RentChargeCandidate(lease, period, cfg));
            }
        }

        if (candidates.Count == 0)
        {
            _logger.LogInformation(
                "RentChargeService created 0 scheduled rent payment(s) through {BusinessDate}",
                today);
            return 0;
        }

        var candidateLeaseIds = candidates
            .Select(c => c.Lease.Id)
            .Distinct()
            .ToList();
        var candidatePeriodKeys = candidates
            .Select(c => c.Period.PeriodKey)
            .Distinct()
            .ToList();

        var existingRows = await _db.Payments
            .AsNoTracking()
            .Where(p => p.LeaseId != null
                && candidateLeaseIds.Contains(p.LeaseId.Value)
                && p.PaymentType == PaymentType.Rent
                && p.PeriodKey != null
                && candidatePeriodKeys.Contains(p.PeriodKey))
            .Select(p => new { LeaseId = p.LeaseId!.Value, p.PeriodKey })
            .ToListAsync(ct);

        var existing = existingRows
            .Select(p => (p.LeaseId, p.PeriodKey!))
            .ToHashSet();

        var notifier = new AutomationNotifier(_db, _publisher);
        var created = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var lease = candidate.Lease;
            var period = candidate.Period;
            var cfg = candidate.Config;
            if (existing.Contains((lease.Id, period.PeriodKey)))
            {
                continue;
            }

            var payment = new Payment
            {
                PortfolioId = lease.PortfolioId,
                LeaseId = lease.Id,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Scheduled,
                Amount = lease.MonthlyRent,
                DueDate = period.DueDate,
                PeriodKey = period.PeriodKey,
                CreatedAt = _timeProvider.UtcNow(),
                UpdatedAt = _timeProvider.UtcNow(),
            };

            _db.Payments.Add(payment);

            // M15: the scheduled-payment row and its outbox notification must commit atomically —
            // a crash between two separate saves could leave a payment without its tenant notice
            // (or vice-versa). Wrap both saves in one EF transaction; the next cycle's idempotency
            // check re-attempts both together if this rolls back.
            var inAppRows = (IReadOnlyList<Notification>)Array.Empty<Notification>();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _db.SaveChangesAsync(ct);

                // Optional notification on exactly the channels enabled for RentCharge in this
                // portfolio (in-app for staff + tenant email/SMS). NotifyTenants stays the gate for
                // tenant-facing email/SMS; the publisher shares this DbContext so inserts enlist here.
                if (cfg.NotifyTenants && period.DueDate.Date >= today && lease.Tenant is { } tenant)
                {
                    var channels = cfg.ResolveChannels(NotificationType.RentCharge);
                    var message = $"Hi {tenant.FirstName}, your rent of {lease.MonthlyRent:C} is due on {period.DueDate:MMMM d}.";
                    var tenantMessage = $"Your rent of {lease.MonthlyRent:C} is due on {period.DueDate:MMMM d}.";
                    inAppRows = await notifier.SendAsync(
                        lease.PortfolioId,
                        channels,
                        new AutomationNotifier.InAppContent(
                            Type: "RentCharge",
                            Title: $"Rent due {period.DueDate:MMM d}",
                            Message: $"{tenant.FirstName} {tenant.LastName}".Trim() + $" — rent of {lease.MonthlyRent:C} due {period.DueDate:MMMM d}.",
                            Severity: "Info",
                            ActionUrl: $"/payments/{payment.Id}",
                            RelatedEntityType: "Payment",
                            RelatedEntityId: payment.Id),
                        new AutomationNotifier.EmailContent(tenant.Email, $"Rent due {period.DueDate:MMM d}", message),
                        new AutomationNotifier.SmsContent(tenant.Phone, message),
                        _timeProvider.UtcNow(),
                        ct,
                        new AutomationNotifier.AudienceTargets(
                            IncludeStaff: true,
                            TenantId: tenant.Id,
                            TenantInApp: new AutomationNotifier.InAppContent(
                                Type: "RentCharge",
                                Title: $"Rent due {period.DueDate:MMM d}",
                                Message: tenantMessage,
                                Severity: "Info",
                                ActionUrl: "/portal/payments",
                                RelatedEntityType: "Payment",
                                RelatedEntityId: payment.Id)));

                    // The in-app rows were added before this save; persist them inside the transaction.
                    if (inAppRows.Count > 0)
                        await _db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
                created++;
                existing.Add((lease.Id, period.PeriodKey));

                foreach (var row in inAppRows)
                    await _dataUpdate.BroadcastEntityUpdateAsync(
                        lease.PortfolioId, "Notification", row.Id, NotificationResponse.FromEntity(row), ct);
            }
            catch (DbUpdateException ex)
            {
                // The partial unique index on (LeaseId, PaymentType, PeriodKey) is a safe backstop
                // against a race between two worker instances. Treat as a no-op and move on.
                await tx.RollbackAsync(ct);
                _logger.LogDebug(
                    ex,
                    "Rent charge already exists for lease {LeaseId} period {PeriodKey} (DB unique violation — skipping)",
                    lease.Id, period.PeriodKey);

                // Detach the failed entity so the context remains usable for the next iteration.
                _db.Entry(payment).State = EntityState.Detached;
                continue;
            }
            catch (Exception ex)
            {
                // Any other failure (e.g. notification enqueue) rolls back the whole unit so we never
                // persist a payment without its notice. The next cycle retries both atomically.
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    ex,
                    "Failed to create rent charge + notice for lease {LeaseId} period {PeriodKey}; rolled back, will retry",
                    lease.Id, period.PeriodKey);

                _db.Entry(payment).State = EntityState.Detached;
                continue;
            }
        }

        _logger.LogInformation(
            "RentChargeService created {Count} scheduled rent payment(s) through {BusinessDate}",
            created, today);

        return created;
    }

    private static DateTime RentChargeGenerationStart(Lease lease)
    {
        var leaseStart = lease.StartDate.Date;
        var trackingStart = lease.RentTrackingStartDate?.Date;
        if (!trackingStart.HasValue || trackingStart.Value < leaseStart)
        {
            return leaseStart;
        }

        return trackingStart.Value;
    }
}
