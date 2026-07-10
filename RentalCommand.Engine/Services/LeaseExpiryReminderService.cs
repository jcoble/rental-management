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
    private const int DefaultLeaseExpiryReminderDays = 60;

    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly INotificationSettingsService _settings;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LeaseExpiryReminderService> _logger;

    public LeaseExpiryReminderService(
        RentalCommandDbContext db,
        IMessagePublisher publisher,
        INotificationSettingsService settings,
        IDataUpdateService dataUpdate,
        TimeProvider timeProvider,
        ILogger<LeaseExpiryReminderService> logger)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _dataUpdate = dataUpdate;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> RemindAsync(CancellationToken ct = default)
    {
        var today = _timeProvider.UtcNow().Date;
        var defaultReminderCutoff = today.AddDays(DefaultLeaseExpiryReminderDays);

        // Look-ahead can differ per portfolio. Push the enabled/window gate into SQL by joining the
        // persisted settings row; missing rows use the same runtime defaults GetRuntimeAsync would create.
        var leases = await (
            from lease in _db.Leases
            join setting in _db.NotificationSettings
                on lease.PortfolioId equals setting.PortfolioId into settings
            from setting in settings.DefaultIfEmpty()
            where lease.Status == LeaseStatus.Active
                  && lease.ExpiryReminderSentAt == null
                  && lease.EndDate >= today
                  && ((setting == null && lease.EndDate <= defaultReminderCutoff)
                      || (setting != null
                          && setting.EnableLeaseExpiryReminders
                          && lease.EndDate <= today.AddDays(setting.LeaseExpiryReminderDays)))
            select lease)
            .Include(l => l.Property)
                .ThenInclude(p => p!.Owner)   // M3: load the owner with the lease (one JOIN) instead of a per-lease Owners.FindAsync; the Include also honors the soft-delete filter that FindAsync bypassed
            .Include(l => l.Tenant)
            .ToListAsync(ct);

        if (leases.Count == 0)
            return 0;

        // M3: batch the owner-email fallback (a linked UserAccount address used when the Owner record has
        // no email) into one query keyed by (OwnerId, PortfolioId), instead of a per-lease UserAccounts
        // lookup inside the loop.
        var ownerIds = leases
            .Select(l => l.Property?.OwnerId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var ownerFallbackEmails = ownerIds.Count == 0
            ? new Dictionary<(int OwnerId, int PortfolioId), string>()
            : await _db.UserAccounts
                .Where(u => u.OwnerId != null && ownerIds.Contains(u.OwnerId.Value) && u.Email != "")
                .GroupBy(u => new { OwnerId = u.OwnerId!.Value, u.PortfolioId })
                .Select(g => new
                {
                    g.Key.OwnerId,
                    g.Key.PortfolioId,
                    Email = g.Min(u => u.Email),
                })
                .ToDictionaryAsync(x => (x.OwnerId, x.PortfolioId), x => x.Email ?? string.Empty, ct);

        var notifier = new AutomationNotifier(_db, _publisher);
        var configCache = new Dictionary<int, NotificationsConfig>();
        var broadcast = new List<(int PortfolioId, Notification Row)>();
        var count = 0;

        foreach (var lease in leases)
        {
            ct.ThrowIfCancellationRequested();

            if (!configCache.TryGetValue(lease.PortfolioId, out var cfg))
            {
                cfg = await _settings.GetRuntimeAsync(lease.PortfolioId, ct);
                configCache[lease.PortfolioId] = cfg;
            }

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
                var owner = lease.Property?.Owner;   // from the Include (soft-delete-filtered)
                email = owner?.Email;
                phone = owner?.Phone;

                // Fallback: the linked UserAccount's email when the Owner record has none (preloaded).
                if (string.IsNullOrWhiteSpace(email) &&
                    ownerFallbackEmails.TryGetValue((ownerId.Value, lease.PortfolioId), out var fallbackEmail))
                {
                    email = fallbackEmail;
                }
            }

            var channels = cfg.ResolveChannels(NotificationType.LeaseExpiry);

            // Nothing to deliver on for this lease: in-app off (or no staff) AND no owner contact for
            // the enabled email/SMS channels. Skip without setting the marker so it retries once a
            // channel/contact becomes available.
            var hasEmailTarget = channels.EnableEmail && !string.IsNullOrWhiteSpace(email);
            var hasSmsTarget = channels.EnableSms && !string.IsNullOrWhiteSpace(phone);
            if (!channels.EnableInApp && !hasEmailTarget && !hasSmsTarget)
            {
                _logger.LogWarning(
                    "No enabled channel/contact for expiry reminder — portfolio {PortfolioId}, lease {LeaseId}; skipping",
                    lease.PortfolioId, lease.Id);
                continue;
            }

            var body = $"Lease {lease.LeaseNumber} for {tenantName} ends on "
                       + $"{lease.EndDate:MMMM d, yyyy} ({daysLeft} days). "
                       + "Consider a renewal or move-out plan.";

            // Set the marker BEFORE publishing so the publisher's SaveChanges commits the outbox
            // row(s) and the marker in one transaction — a crash can't leave one without the other.
            lease.ExpiryReminderSentAt = _timeProvider.UtcNow();
            try
            {
                var inAppRows = await notifier.SendAsync(
                    lease.PortfolioId,
                    channels,
                    new AutomationNotifier.InAppContent(
                        Type: "LeaseExpiry",
                        Title: $"Lease {lease.LeaseNumber} expires {lease.EndDate:MMM d}",
                        Message: body,
                        Severity: "Warning",
                        ActionUrl: $"/leases/{lease.Id}",
                        RelatedEntityType: "Lease",
                        RelatedEntityId: lease.Id),
                    new AutomationNotifier.EmailContent(email, $"Lease {lease.LeaseNumber} expires {lease.EndDate:MMM d}", body),
                    new AutomationNotifier.SmsContent(phone, body),
                    _timeProvider.UtcNow(),
                    ct);

                foreach (var row in inAppRows)
                    broadcast.Add((lease.PortfolioId, row));

                count++;
            }
            catch (Exception ex)
            {
                lease.ExpiryReminderSentAt = null;   // publish failed/rolled back → retry next cycle
                _logger.LogWarning(
                    ex,
                    "Failed to enqueue expiry reminder for portfolio {PortfolioId}, lease {LeaseId}",
                    lease.PortfolioId, lease.Id);
            }
        }

        // Each successful publish already committed its marker atomically with the outbox row(s) (the
        // marker is set before PublishAsync, and the real publisher saves). This trailing save is a
        // no-op in production but persists markers + in-app rows when the publisher is a test double
        // that doesn't save.
        await _db.SaveChangesAsync(ct);

        foreach (var (portfolioId, row) in broadcast)
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, "Notification", row.Id, NotificationResponse.FromEntity(row), ct);

        return count;
    }
}
