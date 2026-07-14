using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Api.Services.Domain;

public class NotificationService : INotificationService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public NotificationService(
        RentalCommandDbContext db, TimeProvider timeProvider, IAtomicUnitOfWork atomic)
    {
        _db = db;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
            query = query.Where(n => !_db.NotificationReadStates.Any(readState =>
                readState.PortfolioId == portfolioId &&
                readState.NotificationId == n.Id &&
                readState.UserId == userId));
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .Select(notification => new NotificationResponse
            {
                Id = notification.Id,
                Type = notification.Type,
                Title = notification.Title,
                Message = notification.Message,
                Severity = notification.Severity,
                ActionUrl = notification.ActionUrl,
                RelatedEntityType = notification.RelatedEntityType,
                RelatedEntityId = notification.RelatedEntityId,
                IsRead = _db.NotificationReadStates.Any(readState =>
                    readState.PortfolioId == portfolioId &&
                    readState.NotificationId == notification.Id &&
                    readState.UserId == userId),
                CreatedAt = notification.CreatedAt,
            })
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.PortfolioId == portfolioId &&
                (n.UserId == null || n.UserId == userId) &&
                !_db.NotificationReadStates.Any(readState =>
                    readState.PortfolioId == portfolioId &&
                    readState.NotificationId == n.Id &&
                    readState.UserId == userId));
        if (!await IsStaffUserAsync(portfolioId, userId, ct))
        {
            query = query.Where(n => n.Type != "TenantMessage");
        }

        return await query.CountAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(
        WorkspaceReadScope scope,
        int notificationId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.MarkRead, notificationId, string.Empty,
            operationKey, new { });
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return outcome.Value.Found;
    }

    public async Task MarkAllAsReadAsync(
        WorkspaceReadScope scope,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.MarkAllRead, 0, string.Empty,
            operationKey, new { });
        await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
    }

    public async Task<NotificationResponse> CreateBroadcastAsync(
        WorkspaceReadScope scope,
        CreateBroadcastNotificationRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.Broadcast, 0, string.Empty, operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return outcome.Value.ResponseJson is not null
            ? JsonSerializer.Deserialize<NotificationResponse>(outcome.Value.ResponseJson)
                ?? throw new InvalidOperationException("Atomic broadcast result snapshot is invalid.")
            : throw new InvalidOperationException("Atomic broadcast result did not contain a response snapshot.");
    }

    private async Task<bool> IsStaffUserAsync(int portfolioId, int userId, CancellationToken ct)
    {
        return await ScopedNotificationRecipientQuery
            .ForWorkspaceMembership(_db, portfolioId, _timeProvider.UtcNow())
            .AnyAsync(candidateUserId => candidateUserId == userId, ct);
    }
}
