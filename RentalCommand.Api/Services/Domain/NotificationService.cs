using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using RentalCommand.Core.Navigation;

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
        WorkspaceReadScope scope,
        NavigationExperience experience,
        bool unreadOnly = false,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default)
    {
        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take, 1, 100);

        var query = AuthorizedNotifications(scope.PortfolioId, scope.UserId);

        if (unreadOnly)
        {
            query = query.Where(n => !_db.NotificationReadStates.Any(readState =>
                readState.PortfolioId == scope.PortfolioId &&
                readState.NotificationId == n.Id &&
                readState.UserId == scope.UserId));
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .Select(ProjectRead(
                scope.PortfolioId, scope.UserId, scope.AccessContextId,
                scope.AccessRevision, experience, _timeProvider.UtcNow()))
            .ToListAsync(ct);
    }

    // Direct service tests predate canonical request scopes. Production callers use the
    // access-bound overload above; this overload preserves their read-state coverage only.
    public Task<IReadOnlyList<NotificationResponse>> ListAsync(
        int portfolioId,
        int userId,
        bool unreadOnly = false,
        int skip = 0,
        int take = 20,
        CancellationToken ct = default) =>
        ListAsync(
            new WorkspaceReadScope(portfolioId, userId, Guid.Empty, 1, 1),
            NavigationExperience.Management,
            unreadOnly, skip, take, ct);

    public async Task<NotificationResponse?> GetAsync(
        WorkspaceReadScope scope,
        NavigationExperience experience,
        int notificationId,
        CancellationToken ct = default)
    {
        return await AuthorizedNotifications(scope.PortfolioId, scope.UserId)
            .Where(notification => notification.Id == notificationId)
            .Select(ProjectRead(
                scope.PortfolioId, scope.UserId, scope.AccessContextId,
                scope.AccessRevision, experience, _timeProvider.UtcNow()))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(int portfolioId, int userId, CancellationToken ct = default)
    {
        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.PortfolioId == portfolioId &&
                (n.UserId == null || n.UserId == userId) &&
                (!_db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId)
                    || _db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId
                        && preference.EnableInApp)) &&
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

    private IQueryable<Notification> AuthorizedNotifications(
        int portfolioId,
        int userId)
    {
        var staffUserIds = ScopedNotificationRecipientQuery
            .ForWorkspaceMembership(_db, portfolioId, _timeProvider.UtcNow());
        var query = _db.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.PortfolioId == portfolioId &&
                (notification.UserId == null || notification.UserId == userId) &&
                (!_db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId)
                    || _db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId
                        && preference.EnableInApp)) &&
                (notification.Type != "TenantMessage" ||
                 staffUserIds.Any(candidateUserId => candidateUserId == userId)));
        return query;
    }

    private Expression<Func<Notification, NotificationResponse>> ProjectRead(
        int portfolioId,
        int userId,
        int accessContextId,
        long accessRevision,
        NavigationExperience experience,
        DateTime nowUtc) =>
        notification => new NotificationResponse
        {
            Id = notification.Id,
            Type = notification.Type,
            Title = notification.Title,
            Message = notification.Message,
            Severity = notification.Severity,
            NavigationIntent =
                notification.NavigationExperience == experience &&
                notification.NavigationAccessContextId == accessContextId &&
                notification.NavigationAccessRevision == accessRevision &&
                notification.NavigationExpiresAtUtc > nowUtc &&
                notification.NavigationDestination != null &&
                notification.NavigationAction != null &&
                notification.NavigationParentResourceKind == null &&
                notification.NavigationParentResourceId == null &&
                notification.NavigationChildResourceKind == null &&
                notification.NavigationChildResourceId == null &&
                (notification.NavigationFallbackDestination == NavigationDestination.Home ||
                 notification.NavigationFallbackDestination == NavigationDestination.Notifications) &&
                (
                    ((notification.NavigationDestination == NavigationDestination.Home ||
                      notification.NavigationDestination == NavigationDestination.Notifications) &&
                     notification.NavigationResourceKind == null &&
                     notification.NavigationResourceId == null) ||
                    (notification.NavigationDestination == NavigationDestination.Message &&
                     notification.NavigationResourceKind == nameof(Conversation) &&
                     notification.NavigationResourceId != null &&
                     (experience == NavigationExperience.Management ||
                      experience == NavigationExperience.Leasing ||
                      experience == NavigationExperience.Tenant) &&
                     _db.Conversations.Any(conversation =>
                         conversation.PortfolioId == portfolioId &&
                         conversation.Id == notification.NavigationResourceId)) ||
                    ((notification.NavigationDestination == NavigationDestination.WorkOrder &&
                      experience == NavigationExperience.Management) ||
                     (notification.NavigationDestination == NavigationDestination.TechnicianWork &&
                      experience == NavigationExperience.Maintenance)) &&
                     notification.NavigationResourceKind == nameof(WorkOrder) &&
                     notification.NavigationResourceId != null &&
                     _db.WorkOrders.Any(workOrder =>
                         workOrder.PortfolioId == portfolioId &&
                         workOrder.Id == notification.NavigationResourceId) ||
                    (notification.NavigationDestination == NavigationDestination.Owners &&
                     experience == NavigationExperience.Management &&
                     notification.NavigationResourceKind == nameof(OwnerEntity) &&
                     notification.NavigationResourceId != null &&
                     _db.OwnerEntities.Any(owner =>
                         owner.PortfolioId == portfolioId &&
                         owner.Id == notification.NavigationResourceId)) ||
                    (notification.NavigationDestination == NavigationDestination.Money &&
                     experience == NavigationExperience.Management &&
                     ((notification.NavigationResourceKind == null &&
                       notification.NavigationResourceId == null) ||
                      (notification.NavigationResourceKind == nameof(BankConnection) &&
                       notification.NavigationResourceId != null &&
                       _db.BankConnections.Any(connection =>
                           connection.PortfolioId == portfolioId &&
                           connection.Id == notification.NavigationResourceId))))
                )
                    ? new NavigationIntentDto
                    {
                        Experience = notification.NavigationExperience.Value,
                        Destination = notification.NavigationDestination.Value,
                        AccessContextId = notification.NavigationAccessContextId.Value,
                        AccessRevision = notification.NavigationAccessRevision.Value,
                        Resource = notification.NavigationResourceKind != null &&
                                   notification.NavigationResourceId != null
                            ? new NavigationResourceDto
                            {
                                Kind = notification.NavigationResourceKind,
                                Id = notification.NavigationResourceId.Value,
                            }
                            : null,
                        ParentResource = notification.NavigationParentResourceKind != null &&
                                         notification.NavigationParentResourceId != null
                            ? new NavigationResourceDto
                            {
                                Kind = notification.NavigationParentResourceKind,
                                Id = notification.NavigationParentResourceId.Value,
                            }
                            : null,
                        ChildResource = notification.NavigationChildResourceKind != null &&
                                        notification.NavigationChildResourceId != null
                            ? new NavigationResourceDto
                            {
                                Kind = notification.NavigationChildResourceKind,
                                Id = notification.NavigationChildResourceId.Value,
                            }
                            : null,
                        Action = notification.NavigationAction.Value,
                        ExpiresAtUtc = notification.NavigationExpiresAtUtc.Value,
                        FallbackDestination = notification.NavigationFallbackDestination.Value,
                    }
                    : null,
            RelatedEntityType = notification.RelatedEntityType,
            RelatedEntityId = notification.RelatedEntityId,
            IsRead = _db.NotificationReadStates.Any(readState =>
                readState.PortfolioId == portfolioId &&
                readState.NotificationId == notification.Id &&
                readState.UserId == userId),
            CreatedAt = notification.CreatedAt,
        };
}
