using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Conversations;

public sealed class SendConversationMessageHandler
    : IAtomicCommandHandler<SendConversationMessageCommand, SendConversationMessageResult>,
      IAtomicReplayAuthorizer<SendConversationMessageCommand>
{
    private const int PreviewMaxLength = 280;
    private static readonly string[] ManagementCapabilities =
        [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage];

    public async Task<SendConversationMessageResult> HandleAsync(
        SendConversationMessageCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.SenderRole is not (ConversationSenderRole.Landlord or ConversationSenderRole.Tenant))
        {
            throw new InvalidOperationException("Unsupported conversation sender role.");
        }

        if (command.SenderRole == ConversationSenderRole.Landlord &&
            command.ManagementAccess is { } managementAccess)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.AuthSession, managementAccess.SessionId, ct);
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.WorkspaceAccessContext, managementAccess.AccessContextId, ct);
        }

        var createsConversation = command.ConversationId is null;
        Conversation conversation;
        Tenant tenant;
        if (command.ConversationId is { } conversationId)
        {
            await attempt.Locking.AcquireAsync(AtomicLockResource.Conversation, conversationId, ct);
            var conversationQuery = attempt.Persistence.Query<Conversation>()
                .Include(candidate => candidate.Tenant)
                .Where(candidate => candidate.Id == conversationId
                    && candidate.PortfolioId == command.PortfolioId
                    && (command.SenderRole != ConversationSenderRole.Tenant
                        || candidate.TenantId == command.TenantId));
            if (command.SenderRole == ConversationSenderRole.Landlord && command.ManagementAccess is not null)
            {
                var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
                conversationQuery = WhereManagementAuthorized(
                    conversationQuery, attempt.Persistence, command, now);
            }

            var existing = await conversationQuery.SingleOrDefaultAsync(ct);
            if (existing?.Tenant is null)
            {
                return NotFound();
            }

            conversation = existing;
            tenant = existing.Tenant;
            conversation.LastMessageAt = command.CreatedAtUtc;
            conversation.LastMessagePreview = Preview(command.Body);
            if (command.SenderRole == ConversationSenderRole.Landlord)
            {
                conversation.TenantUnreadCount += 1;
                if (conversation.WorkOrderId is not null) conversation.TechnicianUnreadCount += 1;
            }
            else
            {
                conversation.LandlordUnreadCount += 1;
                if (conversation.WorkOrderId is not null) conversation.TechnicianUnreadCount += 1;
            }
        }
        else
        {
            var tenantQuery = attempt.Persistence.Query<Tenant>()
                .Where(candidate => candidate.Id == command.TenantId
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.DeletedAt == null);
            if (command.SenderRole == ConversationSenderRole.Landlord && command.ManagementAccess is not null)
            {
                var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
                tenantQuery = WhereManagementAuthorizedForStart(
                    tenantQuery, attempt.Persistence, command, times.WallClockUtc, times.BusinessDate);
            }

            var target = await tenantQuery.SingleOrDefaultAsync(ct);
            if (target is null)
            {
                return NotFound();
            }

            tenant = target;
            conversation = new Conversation
            {
                PortfolioId = command.PortfolioId,
                TenantId = tenant.Id,
                PropertyId = command.PropertyId,
                Subject = command.Subject,
                StartedByLandlord = command.SenderRole == ConversationSenderRole.Landlord,
                CreatedAt = command.CreatedAtUtc,
                LastMessageAt = command.CreatedAtUtc,
                LastMessagePreview = Preview(command.Body),
                LandlordUnreadCount = command.SenderRole == ConversationSenderRole.Tenant ? 1 : 0,
                TenantUnreadCount = command.SenderRole == ConversationSenderRole.Landlord ? 1 : 0,
            };
            attempt.Persistence.Add(conversation);
        }

        var channels = command.SenderRole == ConversationSenderRole.Landlord
            ? NormalizeChannels(command.RequestedChannels, tenant)
            : [];
        var message = new ConversationMessage
        {
            Conversation = conversation,
            SenderRole = command.SenderRole,
            Body = command.Body,
            Channels = channels.Count == 0 ? null : string.Join(',', channels),
            CreatedAt = command.CreatedAtUtc,
        };
        attempt.Persistence.Add(message);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(ConversationMessage),
            message.Id,
            AuditLogOperation.Created,
            NewValues: JsonSerializer.Serialize(new
            {
                message.ConversationId,
                SenderRole = message.SenderRole.ToString(),
                message.Channels,
            }),
            ChangeReason: command.SenderRole == ConversationSenderRole.Landlord
                ? "Landlord conversation message committed with recipient destinations."
                : "Tenant conversation message committed with staff notifications."));
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Conversation),
            conversation.Id,
            createsConversation ? AuditLogOperation.Created : AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new
            {
                conversation.TenantId,
                conversation.Subject,
                conversation.LastMessageAt,
                conversation.LastMessagePreview,
                conversation.LandlordUnreadCount,
                conversation.TenantUnreadCount,
            }),
            ChangeReason: createsConversation
                ? "Conversation opened with its first durable message."
                : "Conversation activity and unread state advanced with a durable message."));

        if (command.SenderRole == ConversationSenderRole.Landlord)
        {
            StageDestinationIntents(attempt, command, tenant, conversation, message, channels);
        }
        var notifications = command.SenderRole == ConversationSenderRole.Landlord
            ? await CreatePortalNotificationsAsync(
                attempt,
                command,
                conversation,
                channels,
                DateOnly.FromDateTime(command.CreatedAtUtc),
                ct)
            : await CreateStaffNotificationsAsync(attempt, command, conversation, tenant, ct);
        if (notifications.Count > 0)
        {
            attempt.Persistence.AddRange(notifications);
            await attempt.FlushBusinessAsync(ct);
            foreach (var notification in notifications)
            {
                attempt.StageSemanticEvent(new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Notification),
                    notification.Id,
                    AuditLogOperation.Created,
                    NewValues: JsonSerializer.Serialize(new
                    {
                        notification.UserId,
                        notification.Type,
                        notification.RelatedEntityId,
                    }),
                    ChangeReason: "Conversation notification committed with its source message."));
            }
        }

        return new SendConversationMessageResult(
            SendConversationMessageOutcome.Applied,
            conversation.Id,
            message.Id,
            notifications.Select(notification => notification.Id).ToArray());
    }

    public async Task AuthorizeReplayAsync(
        SendConversationMessageCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (command.SenderRole != ConversationSenderRole.Landlord || command.ManagementAccess is null)
        {
            return;
        }

        bool authorized;
        if (command.ConversationId is { } conversationId)
        {
            var now = await persistence.ReadDatabaseClockUtcAsync(ct);
            authorized = await WhereManagementAuthorized(
                    persistence.Query<Conversation>().Where(conversation =>
                        conversation.Id == conversationId &&
                        conversation.PortfolioId == command.PortfolioId),
                    persistence,
                    command,
                    now)
                .AnyAsync(ct);
        }
        else
        {
            var times = await persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
            authorized = await WhereManagementAuthorizedForStart(
                    persistence.Query<Tenant>().Where(tenant =>
                        tenant.Id == command.TenantId &&
                        tenant.PortfolioId == command.PortfolioId &&
                        tenant.DeletedAt == null),
                    persistence,
                    command,
                    times.WallClockUtc,
                    times.BusinessDate)
                .AnyAsync(ct);
        }

        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The current workspace access no longer authorizes this conversation command.");
        }
    }

    private static IQueryable<Conversation> WhereManagementAuthorized(
        IQueryable<Conversation> conversations,
        IAtomicPersistenceSession persistence,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var allProperties = AuthorizedAssignments(persistence, command, utcNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var authorizedProperties = AuthorizedProperties(persistence, command, utcNow);

        return conversations.Where(conversation =>
            (conversation.PropertyId == null && allProperties.Any()) ||
            (conversation.PropertyId != null && authorizedProperties.Any(property =>
                property.Id == conversation.PropertyId &&
                property.PortfolioId == conversation.PortfolioId)));
    }

    private static IQueryable<Tenant> WhereManagementAuthorizedForStart(
        IQueryable<Tenant> tenants,
        IAtomicPersistenceSession persistence,
        SendConversationMessageCommand command,
        DateTime utcNow,
        DateOnly businessDate)
    {
        var currentRelationships = persistence.Query<LeaseManagementParty>()
            .Where(party =>
                party.PortfolioId == command.PortfolioId &&
                party.Role != LeaseManagementPartyRole.Guarantor &&
                party.LeaseManagement != null &&
                party.LeaseManagement.CanceledAtUtc == null &&
                party.LeaseManagement.PossessionReturnedAtUtc == null &&
                party.EffectiveFrom <= businessDate &&
                (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate));

        if (command.PropertyId is { } propertyId)
        {
            var authorizedProperties = AuthorizedProperties(persistence, command, utcNow)
                .Where(property => property.Id == propertyId);
            return tenants.Where(tenant =>
                authorizedProperties.Any() &&
                currentRelationships.Any(party =>
                    party.TenantId == tenant.Id &&
                    party.LeaseManagement!.PropertyId == propertyId));
        }

        var allProperties = AuthorizedAssignments(persistence, command, utcNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        return tenants.Where(tenant =>
            allProperties.Any() &&
            !currentRelationships.Any(party => party.TenantId == tenant.Id));
    }

    private static IQueryable<Property> AuthorizedProperties(
        IAtomicPersistenceSession persistence,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var assignments = AuthorizedAssignments(persistence, command, utcNow);
        return persistence.Query<Property>().Where(property =>
            property.PortfolioId == command.PortfolioId &&
            assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 assignment.SelectedProperties.Any(selected =>
                     selected.PortfolioId == property.PortfolioId &&
                     selected.PropertyId == property.Id))));
    }

    private static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        IAtomicPersistenceSession persistence,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var access = command.ManagementAccess
            ?? throw new InvalidOperationException("Management access is required for this query.");
        return persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId &&
            assignment.Status == MembershipRoleAssignmentStatus.Active &&
            assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null &&
            assignment.EffectiveFromUtc <= utcNow &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
            assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContextId == access.AccessContextId &&
            assignment.WorkspaceMembership.PortfolioId == command.PortfolioId &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.UserId == access.UserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == access.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            persistence.Query<AuthSession>().Any(session =>
                session.Id == access.SessionId &&
                session.UserId == access.UserId &&
                session.ActiveAccessContextId == access.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > utcNow) &&
            assignment.RoleProfile != null &&
            assignment.RoleProfile.Capabilities.Any(profileCapability =>
                profileCapability.CapabilityDefinition != null &&
                ManagementCapabilities.Contains(profileCapability.CapabilityDefinition.Key) &&
                (access.RequiredCapabilityKey == null ||
                 profileCapability.CapabilityDefinition.Key == access.RequiredCapabilityKey) &&
                profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));
    }

    private static async Task<List<Notification>> CreateStaffNotificationsAsync(
        IAtomicWriteAttempt attempt,
        SendConversationMessageCommand command,
        Conversation conversation,
        Tenant tenant,
        CancellationToken ct)
    {
        // A tenant message reaches only management users whose effective assignment has rentals.read
        // on the property of an effective issued lease for this tenant. No legacy role-wide fanout,
        // no Owner fanout, and no guessed recipient when an authoritative relationship is absent.
        var userIds = await ScopedNotificationRecipientQuery
            .ForTenantRelationship(
                attempt,
                command.PortfolioId,
                tenant.Id,
                CapabilityKeys.RentalsRead,
                command.CreatedAtUtc)
            .OrderBy(userId => userId)
            .ToListAsync(ct);

        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(tenantName)) tenantName = "Tenant";
        return userIds.Select(userId => new Notification
        {
            PortfolioId = command.PortfolioId,
            UserId = userId,
            Type = "TenantMessage",
            Title = $"New message from {tenantName}",
            Message = Preview(command.Body) ?? conversation.Subject,
            Severity = "Info",
            ActionUrl = $"/messages?conversationId={conversation.Id}",
            RelatedEntityType = nameof(Conversation),
            RelatedEntityId = conversation.Id,
            CreatedAt = command.CreatedAtUtc,
        }).ToList();
    }

    private static async Task<List<Notification>> CreatePortalNotificationsAsync(
        IAtomicWriteAttempt attempt,
        SendConversationMessageCommand command,
        Conversation conversation,
        IReadOnlyCollection<string> channels,
        DateOnly businessDate,
        CancellationToken ct)
    {
        if (!channels.Contains("Portal", StringComparer.Ordinal))
        {
            return [];
        }

        var tenantUserId = await attempt.Persistence.Query<TenantUserAccess>()
            .Where(access => access.PortfolioId == command.PortfolioId
                && access.RevokedAtUtc == null
                && access.AccessContext!.Status == WorkspaceAccessContextStatus.Active
                && access.AccessContext.SuspendedAtUtc == null
                && access.AccessContext.RevokedAtUtc == null
                && access.LeaseManagementParty!.TenantId == conversation.TenantId
                && access.LeaseManagementParty.EffectiveFrom <= businessDate
                && (access.LeaseManagementParty.EffectiveThrough == null
                    || access.LeaseManagementParty.EffectiveThrough >= businessDate))
            .OrderBy(access => access.ApplicationUserId)
            .Select(access => (int?)access.ApplicationUserId)
            .FirstOrDefaultAsync(ct);
        if (tenantUserId is null)
        {
            return [];
        }

        return
        [
            new Notification
            {
                PortfolioId = command.PortfolioId,
                UserId = tenantUserId,
                Type = "TenantNotice",
                Title = conversation.Subject,
                Message = Preview(command.Body) ?? conversation.Subject,
                Severity = "Info",
                ActionUrl = $"/portal/messages?conversation={conversation.Id}",
                RelatedEntityType = nameof(Conversation),
                RelatedEntityId = conversation.Id,
                CreatedAt = command.CreatedAtUtc,
            },
        ];
    }

    private static void StageDestinationIntents(
        IAtomicWriteAttempt attempt,
        SendConversationMessageCommand command,
        Tenant tenant,
        Conversation conversation,
        ConversationMessage message,
        IReadOnlyCollection<string> channels)
    {
        if (channels.Contains("Email", StringComparer.Ordinal))
        {
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new { to = tenant.Email, subject = conversation.Subject, body = command.Body }),
                IdempotencyKey = $"conversation:{conversation.Id}:message:{message.Id}:email:{DestinationHash(tenant.Email!)}",
                CreatedAtUtc = command.CreatedAtUtc,
                NextAttemptAtUtc = command.CreatedAtUtc,
            });
        }

        if (channels.Contains("Sms", StringComparer.Ordinal))
        {
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new { to = tenant.Phone, message = command.Body }),
                IdempotencyKey = $"conversation:{conversation.Id}:message:{message.Id}:sms:{DestinationHash(tenant.Phone!)}",
                CreatedAtUtc = command.CreatedAtUtc,
                NextAttemptAtUtc = command.CreatedAtUtc,
            });
        }
    }

    private static List<string> NormalizeChannels(IEnumerable<string> requested, Tenant tenant)
    {
        var normalized = requested
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel.Trim().ToLowerInvariant() switch
            {
                "portal" => "Portal",
                "email" => "Email",
                "sms" => "Sms",
                _ => null,
            })
            .Where(channel => channel is not null)
            .Select(channel => channel!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (string.IsNullOrWhiteSpace(tenant.Email)) normalized.Remove("Email");
        if (string.IsNullOrWhiteSpace(tenant.Phone)) normalized.Remove("Sms");
        return normalized;
    }

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())));

    private static string? Preview(string body) => string.IsNullOrEmpty(body)
        ? null
        : body.Length <= PreviewMaxLength ? body : body[..PreviewMaxLength];

    private static SendConversationMessageResult NotFound() => new(
        SendConversationMessageOutcome.NotFound, 0, 0, []);
}
