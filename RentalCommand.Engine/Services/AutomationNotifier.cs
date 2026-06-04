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
/// <see cref="NotificationChannelPreference"/>: an in-app <see cref="Notification"/> per staff user
/// (when In-app is on), an email outbox row (when Email is on and an address is present), and/or an
/// SMS outbox row (when SMS is on and a phone is present). Email/SMS go through
/// <see cref="IMessagePublisher"/> so they enlist in the caller's transaction; the in-app rows are
/// added to the shared <see cref="RentalCommandDbContext"/> for the caller to commit. Broadcasting
/// the in-app rows is deferred to the caller (after commit) via the returned list.
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
        CancellationToken ct)
    {
        var created = new List<Notification>();

        if (channels.EnableInApp)
        {
            var staffUserIds = await StaffUserIdsAsync(portfolioId, ct);
            foreach (var userId in staffUserIds)
            {
                var notification = new Notification
                {
                    PortfolioId = portfolioId,
                    UserId = userId,
                    Type = inApp.Type,
                    Title = inApp.Title,
                    Message = inApp.Message,
                    Severity = inApp.Severity,
                    ActionUrl = inApp.ActionUrl,
                    RelatedEntityType = inApp.RelatedEntityType,
                    RelatedEntityId = inApp.RelatedEntityId,
                    CreatedAt = now,
                };
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

        return created;
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
}
