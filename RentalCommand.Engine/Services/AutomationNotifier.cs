using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Fans an automation event out across exactly the channels enabled for its
/// <see cref="NotificationChannelPreference"/>: an in-app <see cref="Notification"/> per targeted
/// staff/tenant user (when In-app is on), an email outbox row (when Email is on and an address is
/// present), an SMS outbox row (when SMS is on and a phone is present), and/or targeted <c>push</c>
/// outbox rows (when Push is on). Email/SMS/push go through <see cref="IMessagePublisher"/> so they
/// enlist in the caller's transaction; the in-app rows are added to the shared
/// <see cref="RentalCommandDbContext"/> for the caller to commit. Broadcasting the in-app rows is
/// deferred to the caller (after commit) via the returned list.
/// </summary>
public sealed class AutomationNotifier
{
    private static readonly string[] StaffRoleNames =
    [
        nameof(UserRole.Admin), nameof(UserRole.Manager), nameof(UserRole.Agent), nameof(UserRole.Owner),
    ];

    private readonly RentalCommandDbContext _db;
    private readonly IMessagePublisher _publisher;

    public AutomationNotifier(RentalCommandDbContext db, IMessagePublisher publisher)
    {
        _db = db;
        _publisher = publisher;
    }

    /// <summary>
    /// Sends one automation event on the enabled channels. Returns the in-app rows added to the
    /// context (empty when In-app is off / there are no staff users) so the caller can broadcast
    /// them after the transaction commits.
    /// </summary>
    public async Task<IReadOnlyList<Notification>> SendAsync(
        int portfolioId,
        NotificationChannelPreference channels,
        InAppContent inApp,
        EmailContent? email,
        SmsContent? sms,
        DateTime now,
        CancellationToken ct,
        AudienceTargets? audience = null)
    {
        var created = new List<Notification>();
        var targets = audience ?? AudienceTargets.StaffOnly;
        var staffUserIds = targets.IncludeStaff
            ? await StaffUserIdsAsync(portfolioId, ct)
            : Array.Empty<int>();
        var tenantUserId = targets.TenantId.HasValue
            ? await TenantUserIdAsync(portfolioId, targets.TenantId.Value, ct)
            : null;
        var tenantInApp = targets.TenantInApp ?? inApp;

        if (channels.EnableInApp)
        {
            foreach (var userId in staffUserIds)
            {
                var notification = BuildNotification(portfolioId, userId, inApp, now);
                _db.Notifications.Add(notification);
                created.Add(notification);
            }

            if (tenantUserId is int tenantNotificationUserId)
            {
                var notification = BuildNotification(portfolioId, tenantNotificationUserId, tenantInApp, now);
                _db.Notifications.Add(notification);
                created.Add(notification);
            }
        }

        if (channels.EnableEmail && email is not null && !string.IsNullOrWhiteSpace(email.To))
        {
            await _publisher.PublishAsync(
                portfolioId,
                "email",
                new { to = email.To, subject = email.Subject, body = email.Body },
                ct);
        }

        if (channels.EnableSms && sms is not null && !string.IsNullOrWhiteSpace(sms.To))
        {
            await _publisher.PublishAsync(
                portfolioId,
                "sms",
                new { to = sms.To, message = sms.Message },
                ct);
        }

        if (channels.EnablePush)
        {
            await PublishPushAsync(portfolioId, staffUserIds, inApp, ct);
            if (tenantUserId is int userId)
                await PublishPushAsync(portfolioId, [userId], tenantInApp, ct);
        }

        return created;
    }

    private static Notification BuildNotification(
        int portfolioId,
        int userId,
        InAppContent content,
        DateTime now) => new()
        {
            PortfolioId = portfolioId,
            UserId = userId,
            Type = content.Type,
            Title = content.Title,
            Message = content.Message,
            Severity = content.Severity,
            ActionUrl = content.ActionUrl,
            RelatedEntityType = content.RelatedEntityType,
            RelatedEntityId = content.RelatedEntityId,
            CreatedAt = now,
        };

    private async Task PublishPushAsync(
        int portfolioId,
        IReadOnlyCollection<int> userIds,
        InAppContent content,
        CancellationToken ct)
    {
        if (userIds.Count == 0)
            return;

        await _publisher.PublishAsync(
            portfolioId,
            "push",
            new
            {
                title = content.Title,
                body = content.Message,
                actionUrl = content.ActionUrl,
                type = content.Type,
                relatedEntityType = content.RelatedEntityType,
                relatedEntityId = content.RelatedEntityId,
                userIds = userIds.Distinct().OrderBy(id => id).ToArray(),
            },
            ct);
    }

    private async Task<IReadOnlyList<int>> StaffUserIdsAsync(int portfolioId, CancellationToken ct) =>
        await (
                from user in _db.Users.AsNoTracking()
                join userRole in _db.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where user.PortfolioId == portfolioId && role.Name != null && StaffRoleNames.Contains(role.Name)
                select user.Id)
            .Distinct()
            .ToListAsync(ct);

    private async Task<int?> TenantUserIdAsync(int portfolioId, int tenantId, CancellationToken ct) =>
        await _db.Users
            .AsNoTracking()
            .Where(u => u.PortfolioId == portfolioId && u.TenantId == tenantId)
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);

    public sealed record InAppContent(
        string Type,
        string Title,
        string Message,
        string Severity = "Info",
        string? ActionUrl = null,
        string? RelatedEntityType = null,
        int? RelatedEntityId = null);

    public sealed record EmailContent(string? To, string Subject, string Body);

    public sealed record SmsContent(string? To, string Message);

    public sealed record AudienceTargets(
        bool IncludeStaff,
        int? TenantId = null,
        InAppContent? TenantInApp = null)
    {
        public static AudienceTargets StaffOnly { get; } = new(IncludeStaff: true);
    }
}
