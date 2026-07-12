using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NotificationService : INotificationService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IDataUpdateService _dataUpdate;

    public NotificationService(
        RentalCommandDbContext db, TimeProvider timeProvider, IDataUpdateService dataUpdate)
    {
        _db = db;
        _timeProvider = timeProvider;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<NotificationResponse>> ListAsync(
        int portfolioId,
        int userId,
        bool unreadOnly = false,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take, 1, 100);

        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.PortfolioId == portfolioId && (n.UserId == null || n.UserId == userId));
        if (!await IsStaffUserAsync(portfolioId, userId, ct))
        {
            query = query.Where(n => n.Type != "TenantMessage");
        }

        if (unreadOnly)
        {
            query = query.Where(n => !n.IsRead);
        }

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .ToListAsync(ct);

        return items.Select(NotificationResponse.FromEntity).ToList();
    }

    public async Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.PortfolioId == portfolioId && (n.UserId == null || n.UserId == userId) && !n.IsRead);
        if (!await IsStaffUserAsync(portfolioId, userId, ct))
        {
            query = query.Where(n => n.Type != "TenantMessage");
        }

        return await query.CountAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(int portfolioId, int userId, int notificationId, CancellationToken ct = default)
    {
        var query = _db.Notifications
            .Where(n =>
                n.Id == notificationId &&
                n.PortfolioId == portfolioId &&
                (n.UserId == null || n.UserId == userId));
        if (!await IsStaffUserAsync(portfolioId, userId, ct))
        {
            query = query.Where(n => n.Type != "TenantMessage");
        }

        var notification = await query.FirstOrDefaultAsync(ct);

        if (notification is null)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = _timeProvider.UtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task MarkAllAsReadAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var query = _db.Notifications
            .Where(n => n.PortfolioId == portfolioId && (n.UserId == null || n.UserId == userId) && !n.IsRead);
        if (!await IsStaffUserAsync(portfolioId, userId, ct))
        {
            query = query.Where(n => n.Type != "TenantMessage");
        }

        await query
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now),
                ct);
    }

    public async Task<NotificationResponse> CreateBroadcastAsync(
        int portfolioId,
        CreateBroadcastNotificationRequest request,
        CancellationToken ct = default)
    {
        var notification = new Notification
        {
            PortfolioId = portfolioId,
            UserId = null,
            Type = "System",
            Title = request.Title.Trim(),
            Message = request.Message.Trim(),
            Severity = NormalizeSeverity(request.Severity),
            ActionUrl = string.IsNullOrWhiteSpace(request.ActionUrl) ? null : request.ActionUrl.Trim(),
            CreatedAt = _timeProvider.UtcNow(),
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(ct);

        // Push the new bell notification live to the portfolio group. Without this the notification
        // store only refreshes on init/open/settings-save, so a broadcast created here (or, via the
        // backplane, by Engine automation) would sit unseen until the next manual refresh.
        var response = NotificationResponse.FromEntity(notification);
        await _dataUpdate.BroadcastEntityUpdateAsync(
            portfolioId, "Notification", notification.Id, response, ct);

        return response;
    }

    private static string NormalizeSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
        {
            return "Info";
        }

        return severity.Trim().ToLowerInvariant() switch
        {
            "success" => "Success",
            "warning" => "Warning",
            "error" => "Error",
            "critical" => "Critical",
            _ => "Info",
        };
    }

    private async Task<bool> IsStaffUserAsync(int portfolioId, int userId, CancellationToken ct)
    {
        var staffRoles = new[] { "Admin", "Manager", "Agent" };

        return await (
                from user in _db.Users.AsNoTracking()
                join userRole in _db.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where user.Id == userId &&
                      user.PortfolioId == portfolioId &&
                      role.Name != null &&
                      staffRoles.Contains(role.Name)
                select user.Id)
            .AnyAsync(ct);
    }
}
