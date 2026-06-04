using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Generates one <c>Scheduled</c> rent <see cref="Payment"/> per active lease per billing period.
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>).
/// </summary>
public sealed class RentChargeService : IRentChargeService
{
    private const string DefaultTimeZoneId = "America/New_York";

    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly INotificationSettingsService _settings;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeZoneInfo _businessTimeZone;
    private readonly ILogger<RentChargeService> _logger;

    public RentChargeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        INotificationSettingsService settings,
        IDataUpdateService dataUpdate,
        IConfiguration configuration,
        ILogger<RentChargeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _dataUpdate = dataUpdate;
        _logger = logger;

        // The landlord's business day rolls over in their LOCAL zone, not UTC. Near month-end an
        // evening (ET) UtcNow is already the next day/month in UTC, which would charge rent for the
        // wrong period — so derive the business "today"/period/due-day in this zone. DB writes stay UTC.
        var tzId = configuration["App:TimeZone"];
        if (string.IsNullOrWhiteSpace(tzId))
            tzId = DefaultTimeZoneId;
        _businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Business "today" in the landlord's local zone (drives period key + due-day math only).
        // Every value WRITTEN to the DB below stays UTC (DateTime.UtcNow / Kind=Utc).
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTimeZone).Date;
        var periodKey = today.ToString("yyyy-MM");

        var leases = await _db.Leases
            .Where(l => l.Status == LeaseStatus.Active)
            .Include(l => l.Tenant)
            .ToListAsync(ct);

        var notifier = new AutomationNotifier(_db, _publisher);
        var configCache = new Dictionary<int, NotificationsConfig>();
        var created = 0;

        foreach (var lease in leases)
        {
            ct.ThrowIfCancellationRequested();

            // Settings are per-portfolio now. Resolve (and memoize) this lease's portfolio config;
            // the master EnableRentCharges flag is the outer gate for that portfolio.
            if (!configCache.TryGetValue(lease.PortfolioId, out var cfg))
            {
                cfg = await _settings.GetRuntimeAsync(lease.PortfolioId, ct);
                configCache[lease.PortfolioId] = cfg;
            }

            if (!cfg.EnableRentCharges)
                continue;

            // Don't create $0 scheduled rent (e.g. unset/peppercorn leases) — nothing to bill.
            if (lease.MonthlyRent <= 0)
                continue;

            // Clamp due-day to actual days in month (e.g. lease.RentDueDay = 31 in February → 28/29).
            // Use the 7-arg constructor to pin Kind=Utc; the 3-arg form produces Kind=Unspecified.
            var dueDay = Math.Min(lease.RentDueDay, DateTime.DaysInMonth(today.Year, today.Month));
            var dueDate = new DateTime(today.Year, today.Month, dueDay, 0, 0, 0, DateTimeKind.Utc);

            // Only act within the lead window: [dueDate - leadDays … dueDate].
            if (today < dueDate.AddDays(-cfg.RentChargeLeadDays) || today > dueDate)
                continue;

            // Idempotency: skip if a rent payment for this period already exists for this lease.
            if (await _db.Payments.AnyAsync(
                    p => p.LeaseId == lease.Id
                         && p.PaymentType == PaymentType.Rent
                         && p.PeriodKey == periodKey,
                    ct))
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
                DueDate = dueDate,
                PeriodKey = periodKey,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
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
                if (cfg.NotifyTenants && lease.Tenant is { } tenant)
                {
                    var channels = cfg.ResolveChannels(NotificationType.RentCharge);
                    var message = $"Hi {tenant.FirstName}, your rent of {lease.MonthlyRent:C} is due on {dueDate:MMMM d}.";
                    inAppRows = await notifier.SendAsync(
                        lease.PortfolioId,
                        channels,
                        new AutomationNotifier.InAppContent(
                            Type: "RentCharge",
                            Title: $"Rent due {dueDate:MMM d}",
                            Message: $"{tenant.FirstName} {tenant.LastName}".Trim() + $" — rent of {lease.MonthlyRent:C} due {dueDate:MMMM d}.",
                            Severity: "Info",
                            ActionUrl: $"/payments/{payment.Id}",
                            RelatedEntityType: "Payment",
                            RelatedEntityId: payment.Id),
                        new AutomationNotifier.EmailContent(tenant.Email, $"Rent due {dueDate:MMM d}", message),
                        new AutomationNotifier.SmsContent(tenant.Phone, message),
                        DateTime.UtcNow,
                        ct);

                    // The in-app rows were added before this save; persist them inside the transaction.
                    if (inAppRows.Count > 0)
                        await _db.SaveChangesAsync(ct);
                }

                await tx.CommitAsync(ct);
                created++;

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
                    lease.Id, periodKey);

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
                    lease.Id, periodKey);

                _db.Entry(payment).State = EntityState.Detached;
                continue;
            }
        }

        _logger.LogInformation(
            "RentChargeService created {Count} scheduled rent payment(s) for period {PeriodKey}",
            created, periodKey);

        return created;
    }
}
