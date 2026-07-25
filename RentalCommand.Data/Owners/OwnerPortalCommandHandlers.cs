using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Owners;

namespace RentalCommand.Data.Owners;

public sealed class DecideOwnerApprovalHandler
    : IAtomicCommandHandler<DecideOwnerApprovalCommand, OwnerPortalCommandResult>,
      IAtomicReplayAuthorizer<DecideOwnerApprovalCommand>
{
    public Task<OwnerPortalCommandResult> HandleAsync(
        DecideOwnerApprovalCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(command.Decision)
            || command.Note?.Trim().Length > 1000)
        {
            throw new DomainValidationException(
                "A valid owner decision and an optional note of at most 1,000 characters are required.");
        }

        return OwnerPortalCommandSupport.RecordAsync(
            attempt,
            command.PortfolioId,
            command.ActorUserId,
            command.ActorSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.NotificationId,
            "OwnerApproval",
            "OwnerApprovalDecision",
            command.Decision == OwnerApprovalDecision.Approved
                ? "Owner approved the request"
                : "Owner declined the request",
            command.Note,
            command.DeliveryIdempotencyKey,
            ct);
    }

    public Task AuthorizeReplayAsync(
        DecideOwnerApprovalCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        OwnerPortalCommandSupport.AuthorizeReplayAsync(
            persistence,
            command.PortfolioId,
            command.ActorUserId,
            command.ActorSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.NotificationId,
            "OwnerApproval",
            ct);
}

public sealed class ReplyToOwnerMessageHandler
    : IAtomicCommandHandler<ReplyToOwnerMessageCommand, OwnerPortalCommandResult>,
      IAtomicReplayAuthorizer<ReplyToOwnerMessageCommand>
{
    public Task<OwnerPortalCommandResult> HandleAsync(
        ReplyToOwnerMessageCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Body) || command.Body.Trim().Length > 4000)
            throw new DomainValidationException("A reply of at most 4,000 characters is required.");

        return OwnerPortalCommandSupport.RecordAsync(
            attempt,
            command.PortfolioId,
            command.ActorUserId,
            command.ActorSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.NotificationId,
            "OwnerMessage",
            "OwnerMessageReply",
            "Owner replied",
            command.Body,
            command.DeliveryIdempotencyKey,
            ct);
    }

    public Task AuthorizeReplayAsync(
        ReplyToOwnerMessageCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        OwnerPortalCommandSupport.AuthorizeReplayAsync(
            persistence,
            command.PortfolioId,
            command.ActorUserId,
            command.ActorSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.NotificationId,
            "OwnerMessage",
            ct);
}

internal static class OwnerPortalCommandSupport
{
    internal static async Task<OwnerPortalCommandResult> RecordAsync(
        IAtomicWriteAttempt attempt,
        int portfolioId,
        int actorUserId,
        Guid actorSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        int sourceNotificationId,
        string requiredSourceType,
        string responseType,
        string responseTitle,
        string? responseBody,
        string deliveryIdempotencyKey,
        CancellationToken ct)
    {
        ValidateEnvelope(portfolioId, actorUserId, actorSessionId, actorAccessContextId,
            actorAccessRevision, sourceNotificationId, deliveryIdempotencyKey);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, actorAccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var source = await AuthorizeSourceAsync(
            attempt.Persistence,
            portfolioId,
            actorUserId,
            actorSessionId,
            actorAccessContextId,
            actorAccessRevision,
            sourceNotificationId,
            requiredSourceType,
            now,
            ct);

        var alreadyRead = await attempt.Persistence.Query<NotificationReadState>()
            .AsNoTracking()
            .AnyAsync(read => read.PortfolioId == portfolioId
                && read.NotificationId == sourceNotificationId
                && read.UserId == actorUserId, ct);
        if (!alreadyRead)
        {
            attempt.Persistence.Add(new NotificationReadState
            {
                PortfolioId = portfolioId,
                NotificationId = sourceNotificationId,
                UserId = actorUserId,
                ReadAt = now,
            });
        }

        var recipients = await StaffRecipientsForOwner(
                attempt.Persistence, portfolioId, source.OwnerEntityId, now)
            .OrderBy(recipient => recipient.UserId)
            .ToListAsync(ct);
        var body = string.IsNullOrWhiteSpace(responseBody)
            ? responseTitle
            : $"{responseTitle}: {responseBody.Trim()}";
        if (body.Length > 1000) body = body[..997] + "...";

        var notifications = recipients.Select(recipient => new Notification
        {
            PortfolioId = portfolioId,
            UserId = recipient.UserId,
            Type = responseType,
            Title = responseTitle,
            Message = body,
            Severity = responseType == "OwnerApprovalDecision" ? "Info" : "Success",
            NavigationExperience = NavigationExperience.Management,
            NavigationDestination = NavigationDestination.Owners,
            NavigationAccessContextId = recipient.AccessContextId,
            NavigationAccessRevision = recipient.AccessRevision,
            NavigationResourceKind = nameof(OwnerEntity),
            NavigationResourceId = source.OwnerEntityId,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(7),
            NavigationFallbackDestination = NavigationDestination.Home,
            RelatedEntityType = "OwnerEntity",
            RelatedEntityId = source.OwnerEntityId,
            CreatedAt = now,
        }).ToArray();
        foreach (var notification in notifications) attempt.Persistence.Add(notification);
        await attempt.FlushBusinessAsync(ct);

        attempt.UseDatabaseWallClockForAudit(now);
        if (!alreadyRead)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                portfolioId,
                nameof(NotificationReadState),
                sourceNotificationId,
                AuditLogOperation.Created,
                actorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    NotificationId = sourceNotificationId,
                    UserId = actorUserId,
                    ReadAt = now,
                }),
                ChangeReason: "Owner acted on a relationship-scoped portal item."), now);
        }
        foreach (var notification in notifications)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                portfolioId,
                nameof(Notification),
                notification.Id,
                AuditLogOperation.Created,
                actorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    notification.UserId,
                    notification.Type,
                    SourceNotificationId = sourceNotificationId,
                    OwnerEntityId = source.OwnerEntityId,
                }),
                ChangeReason: "Owner portal response routed to authorized staff."), now);
        }
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = responseType,
                entityId = sourceNotificationId,
                ownerEntityId = source.OwnerEntityId,
            }),
            IdempotencyKey = $"owner-portal-response:{deliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

        return new OwnerPortalCommandResult(
            sourceNotificationId,
            source.OwnerEntityId,
            notifications.Select(notification => notification.Id).ToArray(),
            now);
    }

    internal static async Task AuthorizeReplayAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int actorUserId,
        Guid actorSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        int sourceNotificationId,
        string requiredSourceType,
        CancellationToken ct)
    {
        ValidateEnvelope(portfolioId, actorUserId, actorSessionId, actorAccessContextId,
            actorAccessRevision, sourceNotificationId, "replay");
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        _ = await AuthorizeSourceAsync(
            persistence,
            portfolioId,
            actorUserId,
            actorSessionId,
            actorAccessContextId,
            actorAccessRevision,
            sourceNotificationId,
            requiredSourceType,
            now,
            ct);
    }

    private static async Task<OwnerSource> AuthorizeSourceAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int actorUserId,
        Guid actorSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        int sourceNotificationId,
        string requiredSourceType,
        DateTime now,
        CancellationToken ct)
    {
        var source = await persistence.Query<Notification>()
            .AsNoTracking()
            .Where(notification =>
                notification.Id == sourceNotificationId
                && notification.PortfolioId == portfolioId
                && notification.UserId == actorUserId
                && notification.Type == requiredSourceType
                && notification.RelatedEntityType == "OwnerEntity"
                && notification.RelatedEntityId != null)
            .Where(notification => persistence.Query<OwnerUserAccess>().Any(access =>
                access.PortfolioId == portfolioId
                && access.ApplicationUserId == actorUserId
                && access.AccessContextId == actorAccessContextId
                && access.OwnerEntityId == notification.RelatedEntityId
                && access.EffectiveFromUtc <= now
                && (access.EffectiveToUtc == null || access.EffectiveToUtc > now)
                && access.RevokedAtUtc == null
                && access.AccessContext != null
                && access.AccessContext.AccessRevision == actorAccessRevision
                && access.AccessContext.Status == WorkspaceAccessContextStatus.Active
                && access.AccessContext.SuspendedAtUtc == null
                && access.AccessContext.RevokedAtUtc == null
                && persistence.Query<AuthSession>().Any(session =>
                    session.Id == actorSessionId
                    && session.UserId == actorUserId
                    && session.ActiveAccessContextId == actorAccessContextId
                    && session.Status == AuthSessionStatus.Active
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > now)))
            .Select(notification => new OwnerSource(notification.RelatedEntityId!.Value))
            .SingleOrDefaultAsync(ct);
        return source ?? throw new UnauthorizedAccessException(
            "The owner portal item is not available in the active relationship.");
    }

    private static IQueryable<StaffRecipient> StaffRecipientsForOwner(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int ownerEntityId,
        DateTime now)
    {
        var ownerPropertyIds = persistence.Query<PropertyOwnership>()
            .Where(ownership => ownership.PortfolioId == portfolioId
                && ownership.OwnerEntityId == ownerEntityId
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null)
            .Select(ownership => ownership.PropertyId);

        return (
            from context in persistence.Query<WorkspaceAccessContext>()
            join membership in persistence.Query<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in persistence.Query<MembershipRoleAssignment>()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where context.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null
                && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= now
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Capabilities.Any(capability =>
                    capability.CapabilityDefinition!.Key == CapabilityKeys.MoneyOwnerReportsRead
                    && capability.CapabilityDefinition.AuthorizationTargetKind
                        == CapabilityAuthorizationTargetKind.Property)
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    && ownerPropertyIds.Any()
                    || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == portfolioId
                        && ownerPropertyIds.Contains(selected.PropertyId)))
            select new StaffRecipient(context.UserId, context.Id, context.AccessRevision))
            .Distinct()
            .TagWith("Owner portal response recipients: owner property scope and owner-report authority");
    }

    private static void ValidateEnvelope(
        int portfolioId,
        int actorUserId,
        Guid actorSessionId,
        int actorAccessContextId,
        long actorAccessRevision,
        int sourceNotificationId,
        string deliveryIdempotencyKey)
    {
        if (portfolioId <= 0 || actorUserId <= 0 || actorSessionId == Guid.Empty
            || actorAccessContextId <= 0 || actorAccessRevision <= 0
            || sourceNotificationId <= 0 || string.IsNullOrWhiteSpace(deliveryIdempotencyKey))
        {
            throw new DomainValidationException("A complete owner portal command is required.");
        }
    }

    private sealed record OwnerSource(int OwnerEntityId);
    private sealed record StaffRecipient(int UserId, int AccessContextId, long AccessRevision);
}
