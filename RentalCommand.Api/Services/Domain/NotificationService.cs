using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NotificationService : INotificationService
{
    private readonly RentalCommandDbContext _db;

    public NotificationService(RentalCommandDbContext db)
    {
        _db = db;
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

    public Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        return _db.Notifications
            .AsNoTracking()
            .Where(n => n.PortfolioId == portfolioId && (n.UserId == null || n.UserId == userId) && !n.IsRead)
            .CountAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(int portfolioId, int userId, int notificationId, CancellationToken ct = default)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n =>
                n.Id == notificationId &&
                n.PortfolioId == portfolioId &&
                (n.UserId == null || n.UserId == userId),
                ct);

        if (notification is null)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task MarkAllAsReadAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.Notifications
            .Where(n => n.PortfolioId == portfolioId && (n.UserId == null || n.UserId == userId) && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now),
                ct);
    }

    public async Task<NotificationEmailResponse> GetNotificationEmailAsync(int portfolioId, CancellationToken ct = default)
    {
        var settings = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => p.Settings)
            .FirstOrDefaultAsync(ct);

        return new NotificationEmailResponse { Email = ReadNotificationEmail(settings) };
    }

    public async Task<NotificationEmailResponse?> SetNotificationEmailAsync(int portfolioId, string? email, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios.FirstOrDefaultAsync(p => p.Id == portfolioId, ct);
        if (portfolio is null)
        {
            return null;
        }

        var trimmed = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        portfolio.Settings = WriteNotificationEmail(portfolio.Settings, trimmed);
        portfolio.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new NotificationEmailResponse { Email = trimmed };
    }

    private static string? ReadNotificationEmail(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("notifications", out var notifications) &&
                notifications.ValueKind == JsonValueKind.Object &&
                notifications.TryGetProperty("email", out var email) &&
                email.ValueKind == JsonValueKind.String)
            {
                return string.IsNullOrWhiteSpace(email.GetString()) ? null : email.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string WriteNotificationEmail(string? settingsJson, string? email)
    {
        Dictionary<string, object?> root;
        try
        {
            root = string.IsNullOrWhiteSpace(settingsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(settingsJson) ?? new Dictionary<string, object?>();
        }
        catch
        {
            root = new Dictionary<string, object?>();
        }

        Dictionary<string, object?> notifications;
        if (root.TryGetValue("notifications", out var existing) &&
            existing is JsonElement element &&
            element.ValueKind == JsonValueKind.Object)
        {
            notifications = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText()) ?? new Dictionary<string, object?>();
        }
        else if (existing is Dictionary<string, object?> existingDict)
        {
            notifications = existingDict;
        }
        else
        {
            notifications = new Dictionary<string, object?>();
        }

        notifications["email"] = email;
        root["notifications"] = notifications;
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
