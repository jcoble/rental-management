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

public static class OwnerPortalWriteSupport
{
    public static TransactionalWrite<TCommand, OwnerPortalCommandResult> Write<TCommand>(
        RentalCommandDbContext db,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
    {
        object write = command switch
        {
            DecideOwnerApprovalCommand value => Build(
                "owner-portal.approval-decision", "owner-portal.approval-decision.v1", value,
                new DecideOwnerApprovalRule(db).ExecuteAsync,
                new DecideOwnerApprovalRule(db).AuthorizeReplayAsync),
            ReplyToOwnerMessageCommand value => Build(
                "owner-portal.message-reply", "owner-portal.message-reply.v1", value,
                new ReplyToOwnerMessageRule(db).ExecuteAsync,
                new ReplyToOwnerMessageRule(db).AuthorizeReplayAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return (TransactionalWrite<TCommand, OwnerPortalCommandResult>)write;
    }

    private static TransactionalWrite<TCommand, OwnerPortalCommandResult> Build<TCommand>(
        string operationName,
        string resultContract,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<OwnerPortalCommandResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData => new(
            operationName,  command, resultContract,
            WriteLockPlan.None, executeAsync, authorizeReplayAsync);

}

public sealed class DecideOwnerApprovalRule
{
    private readonly RentalCommandDbContext _db;

    public DecideOwnerApprovalRule(RentalCommandDbContext db) => _db = db;

    public Task<OwnerPortalCommandResult> ExecuteAsync(
        DecideOwnerApprovalCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(command.Decision)
            || command.Note?.Trim().Length > 1000)
        {
            throw new DomainValidationException(
                "A valid owner decision and an optional note of at most 1,000 characters are required.");
        }

        return OwnerPortalCommandSupport.RecordAsync(
            _db,
            context,
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
        DecideOwnerApprovalCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        OwnerPortalCommandSupport.AuthorizeReplayAsync(
            _db,
            command.PortfolioId,
            command.ActorUserId,
            command.ActorSessionId,
            command.ActorAccessContextId,
            command.ActorAccessRevision,
            command.NotificationId,
            "OwnerApproval",
            ct);
}

public sealed class ReplyToOwnerMessageRule
{
    private readonly RentalCommandDbContext _db;

    public ReplyToOwnerMessageRule(RentalCommandDbContext db) => _db = db;

    public Task<OwnerPortalCommandResult> ExecuteAsync(
        ReplyToOwnerMessageCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.Body) || command.Body.Trim().Length > 4000)
            throw new DomainValidationException("A reply of at most 4,000 characters is required.");

        return OwnerPortalCommandSupport.RecordAsync(
            _db,
            context,
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
        ReplyToOwnerMessageCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        OwnerPortalCommandSupport.AuthorizeReplayAsync(
            _db,
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
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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
        await context.AcquireLockAsync(
            "WorkspaceAccessContext", actorAccessContextId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var source = await AuthorizeSourceAsync(
            db,
            portfolioId,
            actorUserId,
            actorSessionId,
            actorAccessContextId,
            actorAccessRevision,
            sourceNotificationId,
            requiredSourceType,
            now,
            ct);

        var alreadyRead = await db.Set<NotificationReadState>()
            .AsNoTracking()
            .AnyAsync(read => read.PortfolioId == portfolioId
                && read.NotificationId == sourceNotificationId
                && read.UserId == actorUserId, ct);
        if (!alreadyRead)
        {
            db.Add(new NotificationReadState
            {
                PortfolioId = portfolioId,
                NotificationId = sourceNotificationId,
                UserId = actorUserId,
                ReadAt = now,
            });
        }

        var recipients = await StaffRecipientsForOwner(
                db, portfolioId, source.OwnerEntityId, now)
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
        foreach (var notification in notifications) db.Add(notification);
        await context.FlushBusinessAsync(ct);

        context.UseDatabaseWallClockForAudit(now);
        if (!alreadyRead)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
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
            context.StageSemanticEvent(new AtomicSemanticAudit(
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
        context.StageOutbox(new OutboxMessage
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
        RentalCommandDbContext db,
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
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await AuthorizeSourceAsync(
            db,
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
        RentalCommandDbContext db,
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
        var source = await db.Set<Notification>()
            .AsNoTracking()
            .Where(notification =>
                notification.Id == sourceNotificationId
                && notification.PortfolioId == portfolioId
                && notification.UserId == actorUserId
                && notification.Type == requiredSourceType
                && notification.RelatedEntityType == "OwnerEntity"
                && notification.RelatedEntityId != null)
            .Where(notification => db.Set<OwnerUserAccess>().Any(access =>
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
                && db.Set<AuthSession>().Any(session =>
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
        RentalCommandDbContext db,
        int portfolioId,
        int ownerEntityId,
        DateTime now)
    {
        var ownerPropertyIds = db.Set<PropertyOwnership>()
            .Where(ownership => ownership.PortfolioId == portfolioId
                && ownership.OwnerEntityId == ownerEntityId
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now)
                && ownership.Property != null
                && ownership.Property.DeletedAt == null)
            .Select(ownership => ownership.PropertyId);

        return (
            from context in db.Set<WorkspaceAccessContext>()
            join membership in db.Set<WorkspaceMembership>()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join assignment in db.Set<MembershipRoleAssignment>()
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
