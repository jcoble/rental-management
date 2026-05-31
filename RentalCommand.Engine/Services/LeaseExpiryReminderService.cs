using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Sends one-time lease-expiry reminders to property owners for all active leases expiring
/// within the configured look-ahead window (<see cref="NotificationsConfig.LeaseExpiryReminderDays"/>).
///
/// Owner-contact resolution order:
///   1. <c>Owner.Email</c>  → email message (preferred).
///   2. <c>UserAccount.Email</c> linked to the same OwnerId + PortfolioId → email fallback.
///   3. <c>Owner.Phone</c>  → SMS (if no email found from steps 1–2).
///   4. No contact found   → log a warning, skip setting <c>ExpiryReminderSentAt</c> so the
///      reminder will be retried once contact details are added.
///
/// Idempotency: <c>Lease.ExpiryReminderSentAt</c> is set only after a successful publish.
/// A single <c>SaveChangesAsync</c> at the end persists all markers in one round-trip.
/// </summary>
public sealed class LeaseExpiryReminderService : ILeaseExpiryReminderService
{
    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly NotificationsConfig _cfg;
    private readonly ILogger<LeaseExpiryReminderService> _logger;

    public LeaseExpiryReminderService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        IOptions<NotificationsConfig> options,
        ILogger<LeaseExpiryReminderService> logger)
    {
        _db = db;
        _publisher = publisher;
        _cfg = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> RemindAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableLeaseExpiryReminders)
        {
            _logger.LogDebug("lease expiry reminders disabled");
            return 0;
        }

        var today = DateTime.UtcNow.Date;
        var windowEnd = today.AddDays(_cfg.LeaseExpiryReminderDays);

        var leases = await _db.Leases
            .Where(l => l.Status == LeaseStatus.Active
                        && l.ExpiryReminderSentAt == null
                        && l.EndDate >= today
                        && l.EndDate <= windowEnd)
            .Include(l => l.Property)
            .Include(l => l.Tenant)
            .ToListAsync(ct);

        if (leases.Count == 0)
            return 0;

        var count = 0;

        foreach (var lease in leases)
        {
            ct.ThrowIfCancellationRequested();

            var daysLeft = (lease.EndDate.Date - today).Days;
            var tenantName = lease.Tenant is { } t
                ? $"{t.FirstName} {t.LastName}".Trim()
                : "your tenant";

            // --- Resolve owner contact ---
            string? email = null;
            string? phone = null;

            var ownerId = lease.Property?.OwnerId;
            if (ownerId.HasValue)
            {
                var owner = await _db.Owners.FindAsync([ownerId.Value], cancellationToken: ct);
                email = owner?.Email;
                phone = owner?.Phone;

                // Fallback: try the linked UserAccount for an email when the Owner record has none.
                if (string.IsNullOrWhiteSpace(email))
                {
                    email = await _db.UserAccounts
                        .Where(u => u.OwnerId == ownerId.Value && u.PortfolioId == lease.PortfolioId)
                        .Select(u => u.Email)
                        .FirstOrDefaultAsync(ct);
                }
            }

            // --- Send or skip ---
            if (!string.IsNullOrWhiteSpace(email))
            {
                try
                {
                    await _publisher.PublishAsync(
                        lease.PortfolioId,
                        "email",
                        new
                        {
                            to = email,
                            subject = $"Lease {lease.LeaseNumber} expires {lease.EndDate:MMM d}",
                            body = $"Lease {lease.LeaseNumber} for {tenantName} ends on "
                                   + $"{lease.EndDate:MMMM d, yyyy} ({daysLeft} days). "
                                   + "Consider a renewal or move-out plan.",
                        },
                        ct);

                    lease.ExpiryReminderSentAt = DateTime.UtcNow;
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to enqueue expiry-reminder email for portfolio {PortfolioId}, lease {LeaseId}",
                        lease.PortfolioId, lease.Id);
                    // Do not set ExpiryReminderSentAt — the worker will retry next cycle.
                }
            }
            else if (!string.IsNullOrWhiteSpace(phone))
            {
                try
                {
                    await _publisher.PublishAsync(
                        lease.PortfolioId,
                        "sms",
                        new
                        {
                            to = phone,
                            message = $"Lease {lease.LeaseNumber} for {tenantName} ends on "
                                      + $"{lease.EndDate:MMMM d, yyyy} ({daysLeft} days). "
                                      + "Consider a renewal or move-out plan.",
                        },
                        ct);

                    lease.ExpiryReminderSentAt = DateTime.UtcNow;
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to enqueue expiry-reminder SMS for portfolio {PortfolioId}, lease {LeaseId}",
                        lease.PortfolioId, lease.Id);
                    // Do not set ExpiryReminderSentAt — the worker will retry next cycle.
                }
            }
            else
            {
                _logger.LogWarning(
                    "No owner contact found for portfolio {PortfolioId}, lease {LeaseId} — skipping reminder",
                    lease.PortfolioId, lease.Id);
                // ExpiryReminderSentAt intentionally not set; reminder will send once contact exists.
            }
        }

        // Persist all ExpiryReminderSentAt markers in one round-trip.
        await _db.SaveChangesAsync(ct);

        return count;
    }
}
