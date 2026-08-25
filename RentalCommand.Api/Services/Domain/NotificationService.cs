using System.Text.Json;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using NotificationCrudWriteRequest = RentalCommand.Core.Atomic.TransactionalWriteDefaults.AuthorizationScopedRequest<RentalCommand.Api.Services.Domain.NotificationCrudOperation>;
using RentalCommand.Core.Navigation;

namespace RentalCommand.Api.Services.Domain;

public class NotificationService : INotificationService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public NotificationService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IRequestWriteExecutor writes)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
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

        var query = AuthorizedNotifications(scope.PortfolioId, scope.UserId, experience);

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

    public async Task<NotificationResponse?> GetAsync(
        WorkspaceReadScope scope,
        NavigationExperience experience,
        int notificationId,
        CancellationToken ct = default)
    {
        return await AuthorizedNotifications(scope.PortfolioId, scope.UserId, experience)
            .Where(notification => notification.Id == notificationId)
            .Select(ProjectRead(
                scope.PortfolioId, scope.UserId, scope.AccessContextId,
                scope.AccessRevision, experience, _timeProvider.UtcNow()))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(
        WorkspaceReadScope scope,
        NavigationExperience experience,
        CancellationToken ct = default)
    {
        var query = AuthorizedNotifications(scope.PortfolioId, scope.UserId, experience)
            .Where(n => !_db.NotificationReadStates.Any(readState =>
                readState.PortfolioId == scope.PortfolioId &&
                readState.NotificationId == n.Id &&
                readState.UserId == scope.UserId));

        return await query.CountAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(
        WorkspaceReadScope scope,
        int notificationId,
        string operationKey,
        CancellationToken ct = default)
    {
        var request = TransactionalWriteDefaults.Request(
            scope,
            NotificationCrudOperation.MarkRead,
            notificationId,
            string.Empty,
            operationKey,
            new { });
        var write = NotificationCrudWriteSupport.Write(
            request, MarkAsReadAsync, AuthorizeNotificationCrudReplayAsync);
        var outcome = await RequireWrites().ExecuteAsync(
            NotificationCrudWriteSupport.IdempotencyKey(request), write, ct);
        return outcome.Value.Found;
    }

    public async Task MarkAllAsReadAsync(
        WorkspaceReadScope scope,
        string operationKey,
        CancellationToken ct = default)
    {
        var request = TransactionalWriteDefaults.Request(
            scope,
            NotificationCrudOperation.MarkAllRead,
            0,
            string.Empty,
            operationKey,
            new { });
        var write = NotificationCrudWriteSupport.Write(
            request, MarkAllAsReadAsync, AuthorizeNotificationCrudReplayAsync);
        await RequireWrites().ExecuteAsync(
            NotificationCrudWriteSupport.IdempotencyKey(request), write, ct);
    }

    private async Task<AtomicNotificationMutationResult> MarkAsReadAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await NotificationCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var isStaff = await IsStaffAsync(request, now, ct);
        var notification = await _db.Notifications.SingleOrDefaultAsync(candidate =>
            candidate.Id == request.EntityId
            && candidate.PortfolioId == request.PortfolioId
            && (candidate.UserId == null || candidate.UserId == request.ActorUserId)
            && (isStaff || candidate.Type != "TenantMessage"), ct);
        if (notification is null)
        {
            return new AtomicNotificationMutationResult(false, false, 0, 0);
        }

        if (await AtomicNotificationPersistence.MarkReadAsync(
                _db,
                context,
                request.PortfolioId,
                notification.Id,
                request.ActorUserId,
                isStaff,
                now,
                ct))
        {
            context.StageSemanticEvent(
                TransactionalWriteDefaults.Audit(
                    request,
                    nameof(NotificationReadState),
                    AuditLogOperation.Created,
                    "Notification marked read",
                    notification.Id),
                now);
            TransactionalWriteDefaults.StageDataUpdate(
                request, context, nameof(Notification), notification.Id, now);
            return new AtomicNotificationMutationResult(true, true, notification.Id, 1);
        }

        return new AtomicNotificationMutationResult(true, false, notification.Id, 0);
    }

    private async Task<AtomicNotificationMutationResult> MarkAllAsReadAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var now = await NotificationCrudWriteSupport.BeginExecutionAsync(request, _db, context, ct);
        var isStaff = await IsStaffAsync(request, now, ct);
        var result = await AtomicNotificationPersistence.MarkAllReadAsync(
            _db,
            context,
            request.PortfolioId,
            request.ActorUserId,
            isStaff,
            now,
            ct);
        if (result.Count > 0)
        {
            var notificationId = result.NotificationId
                ?? throw new InvalidOperationException(
                    "Mark-all-read inserted rows without returning a notification id.");
            context.StageSemanticEvent(
                TransactionalWriteDefaults.Audit(
                    request,
                    nameof(NotificationReadState),
                    AuditLogOperation.Created,
                    $"{result.Count} notifications marked read",
                    notificationId),
                now);
            TransactionalWriteDefaults.StageDataUpdate(
                request, context, nameof(Notification), notificationId, now);
        }

        return new AtomicNotificationMutationResult(true, result.Count > 0, 0, result.Count);
    }

    private Task<bool> IsStaffAsync(
        NotificationCrudWriteRequest request,
        DateTime now,
        CancellationToken ct) =>
        _db.WorkspaceMemberships.AsNoTracking().AnyAsync(membership =>
            membership.PortfolioId == request.PortfolioId
            && membership.AccessContextId == request.AccessContextId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null
            && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= now
            && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now), ct);

    private Task AuthorizeNotificationCrudReplayAsync(
        NotificationCrudWriteRequest request,
        IAtomicCommandContext context,
        CancellationToken ct) =>
        NotificationCrudWriteSupport.AuthorizeReplayAsync(request, _db, context, ct);

    private IRequestWriteExecutor RequireWrites() => _writes;

    public async Task<NotificationResponse> CreateBroadcastAsync(
        WorkspaceReadScope scope,
        CreateBroadcastNotificationRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.Broadcast, 0, string.Empty, operationKey, request);
        var outcome = await _writes.ExecuteExactAsync(
            AtomicNotificationMutation.Identity(command).IdempotencyKey,
            AtomicNotificationMutation.Write(_db, command), ct);
        return outcome.Value.ResponseJson is not null
            ? JsonSerializer.Deserialize<NotificationResponse>(outcome.Value.ResponseJson)
                ?? throw new InvalidOperationException("Atomic broadcast result snapshot is invalid.")
            : throw new InvalidOperationException("Atomic broadcast result did not contain a response snapshot.");
    }

    private IQueryable<Notification> AuthorizedNotifications(
        int portfolioId,
        int userId,
        NavigationExperience? experience)
    {
        var staffUserIds = ScopedNotificationRecipientQuery
            .ForWorkspaceMembership(_db, portfolioId, _timeProvider.UtcNow());
        var query = _db.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.PortfolioId == portfolioId &&
                ((notification.UserId == userId) ||
                 (notification.UserId == null &&
                    staffUserIds.Any(candidateUserId => candidateUserId == userId) &&
                    (notification.NavigationExperience == experience ||
                     notification.NavigationExperience == null &&
                     (experience == null ||
                      experience == NavigationExperience.Management ||
                      experience == NavigationExperience.Leasing ||
                      experience == NavigationExperience.Maintenance)))) &&
                (!_db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId)
                    || _db.UserAlertPreferences.Any(preference =>
                        preference.PortfolioId == portfolioId && preference.UserId == userId
                        && preference.EnableInApp)) &&
                (notification.Type != "TenantMessage" ||
                 staffUserIds.Any(candidateUserId => candidateUserId == userId)))
            .Where(IsVisibleAsOfBusinessDate());
        return query;
    }

    private Expression<Func<Notification, bool>> IsVisibleAsOfBusinessDate() =>
        notification => notification.Type != "ScheduledRentCharge" ||
            notification.RelatedEntityType != nameof(TenantLedgerEntry) ||
            notification.RelatedEntityId == null ||
            _db.TenantLedgerEntries.Any(entry =>
                entry.PortfolioId == notification.PortfolioId &&
                entry.Id == notification.RelatedEntityId.Value &&
                entry.EffectiveOn <= BusinessDateDbFunction.ForPortfolio(notification.PortfolioId));

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
                (notification.NavigationFallbackDestination == NavigationDestination.Home ||
                 notification.NavigationFallbackDestination == NavigationDestination.Notifications) &&
                (
                    ((notification.NavigationDestination == NavigationDestination.Home ||
                      notification.NavigationDestination == NavigationDestination.Notifications) &&
                     notification.NavigationResourceKind == null &&
                     notification.NavigationResourceId == null &&
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null) ||
                    (notification.NavigationDestination == NavigationDestination.Message &&
                     notification.NavigationResourceKind == nameof(Conversation) &&
                     notification.NavigationResourceId != null &&
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
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
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
                     _db.WorkOrders.Any(workOrder =>
                         workOrder.PortfolioId == portfolioId &&
                         workOrder.Id == notification.NavigationResourceId) ||
                    (notification.NavigationDestination == NavigationDestination.Owners &&
                     experience == NavigationExperience.Management &&
                     notification.NavigationResourceKind == nameof(OwnerEntity) &&
                     notification.NavigationResourceId != null &&
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
                     _db.OwnerEntities.Any(owner =>
                         owner.PortfolioId == portfolioId &&
                         owner.Id == notification.NavigationResourceId)) ||
                    (notification.NavigationDestination == NavigationDestination.Money &&
                     experience == NavigationExperience.Management &&
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
                     ((notification.NavigationResourceKind == null &&
                       notification.NavigationResourceId == null) ||
                      (notification.NavigationResourceKind == nameof(BankConnection) &&
                       notification.NavigationResourceId != null &&
                       _db.BankConnections.Any(connection =>
                           connection.PortfolioId == portfolioId &&
                           connection.Id == notification.NavigationResourceId)))) ||
                    (notification.NavigationDestination == NavigationDestination.TenantLedgerEntry &&
                     experience == NavigationExperience.Tenant &&
                     notification.NavigationResourceKind == nameof(TenantLedgerEntry) &&
                     notification.NavigationResourceId != null &&
                     notification.NavigationParentResourceKind == nameof(TenantAccount) &&
                     notification.NavigationParentResourceId != null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
                     _db.TenantLedgerEntries.Any(entry =>
                         entry.PortfolioId == portfolioId &&
                         entry.Id == notification.NavigationResourceId &&
                         entry.TenantAccountId == notification.NavigationParentResourceId) &&
                     _db.EffectiveTenantAccess.Any(access =>
                         access.PortfolioId == portfolioId &&
                         access.UserId == userId &&
                         access.AccessContextId == accessContextId &&
                         access.AccessRevision == accessRevision &&
                         access.TenantAccountId == notification.NavigationParentResourceId))
                    ||
                    (notification.NavigationDestination == NavigationDestination.TenantAccount &&
                     experience == NavigationExperience.Tenant &&
                     notification.NavigationResourceKind == nameof(TenantAccount) &&
                     notification.NavigationResourceId != null &&
                     notification.NavigationParentResourceKind == null &&
                     notification.NavigationParentResourceId == null &&
                     notification.NavigationChildResourceKind == null &&
                     notification.NavigationChildResourceId == null &&
                     _db.EffectiveTenantAccess.Any(access =>
                         access.PortfolioId == portfolioId &&
                         access.UserId == userId &&
                         access.AccessContextId == accessContextId &&
                         access.AccessRevision == accessRevision &&
                         access.TenantAccountId == notification.NavigationResourceId))
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
