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
/// Generates one <c>Scheduled</c> rent <see cref="Payment"/> per active lease per billing period.
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>).
/// </summary>
public sealed class RentChargeService : IRentChargeService
{
    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly NotificationsConfig _cfg;
    private readonly ILogger<RentChargeService> _logger;

    public RentChargeService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        IOptions<NotificationsConfig> options,
        ILogger<RentChargeService> logger)
    {
        _db = db;
        _publisher = publisher;
        _cfg = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableRentCharges)
        {
            _logger.LogDebug("rent charges disabled");
            return 0;
        }

        var today = DateTime.UtcNow.Date;
        var periodKey = today.ToString("yyyy-MM");

        var leases = await _db.Leases
            .Where(l => l.Status == LeaseStatus.Active)
            .Include(l => l.Tenant)
            .ToListAsync(ct);

        var created = 0;

        foreach (var lease in leases)
        {
            ct.ThrowIfCancellationRequested();

            // Clamp due-day to actual days in month (e.g. lease.RentDueDay = 31 in February → 28/29).
            // Use the 7-arg constructor to pin Kind=Utc; the 3-arg form produces Kind=Unspecified.
            var dueDay = Math.Min(lease.RentDueDay, DateTime.DaysInMonth(today.Year, today.Month));
            var dueDate = new DateTime(today.Year, today.Month, dueDay, 0, 0, 0, DateTimeKind.Utc);

            // Only act within the lead window: [dueDate - leadDays … dueDate].
            if (today < dueDate.AddDays(-_cfg.RentChargeLeadDays) || today > dueDate)
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

            try
            {
                await _db.SaveChangesAsync(ct);
                created++;
            }
            catch (DbUpdateException ex)
            {
                // The partial unique index on (LeaseId, PaymentType, PeriodKey) is a safe backstop
                // against a race between two worker instances. Treat as a no-op and move on.
                _logger.LogDebug(
                    ex,
                    "Rent charge already exists for lease {LeaseId} period {PeriodKey} (DB unique violation — skipping)",
                    lease.Id, periodKey);

                // Detach the failed entity so the context remains usable for the next iteration.
                _db.Entry(payment).State = EntityState.Detached;
                continue;
            }

            // Optional tenant notification (email preferred; fall back to SMS).
            if (_cfg.NotifyTenants && lease.Tenant is { } tenant)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tenant.Email))
                    {
                        await _publisher.PublishAsync(
                            lease.PortfolioId,
                            "email",
                            new
                            {
                                to = tenant.Email,
                                subject = $"Rent due {dueDate:MMM d}",
                                body = $"Hi {tenant.FirstName}, your rent of {lease.MonthlyRent:C} is due on {dueDate:MMMM d}.",
                            },
                            ct);
                    }
                    else if (!string.IsNullOrWhiteSpace(tenant.Phone))
                    {
                        await _publisher.PublishAsync(
                            lease.PortfolioId,
                            "sms",
                            new
                            {
                                to = tenant.Phone,
                                message = $"Hi {tenant.FirstName}, your rent of {lease.MonthlyRent:C} is due on {dueDate:MMMM d}.",
                            },
                            ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to enqueue rent-due notice for tenant {TenantId} (lease {LeaseId})",
                        tenant.Id, lease.Id);
                    // Notification failure must not roll back the already-saved payment.
                }
            }
        }

        _logger.LogInformation(
            "RentChargeService created {Count} scheduled rent payment(s) for period {PeriodKey}",
            created, periodKey);

        return created;
    }
}
