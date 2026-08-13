using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Conversations;

public sealed class SendConversationMessageHandler
    : IAtomicCommandHandler<SendConversationMessageCommand, SendConversationMessageResult>
{
    private readonly RentalCommandDbContext _db;

    public SendConversationMessageHandler(RentalCommandDbContext db) => _db = db;

    private const int PreviewMaxLength = 280;
    private static readonly string[] ManagementCapabilities =
        [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage];

    public async Task<SendConversationMessageResult> HandleAsync(
        SendConversationMessageCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.SenderRole is not (ConversationSenderRole.Landlord or ConversationSenderRole.Tenant))
        {
            throw new InvalidOperationException("Unsupported conversation sender role.");
        }

        if (command.SenderRole == ConversationSenderRole.Landlord &&
            command.ManagementAccess is { } managementAccess)
        {
            await context.AcquireLockAsync(
                "AuthSession", managementAccess.SessionId, ct);
            await context.AcquireLockAsync(
                "WorkspaceAccessContext", managementAccess.AccessContextId, ct);
        }

        var createsConversation = command.ConversationId is null;
        Conversation conversation;
        Tenant tenant;
        if (command.ConversationId is { } conversationId)
        {
            var linkedWorkOrderId = await _db.Set<Conversation>()
                .AsNoTracking()
                .Where(candidate => candidate.Id == conversationId &&
                    candidate.PortfolioId == command.PortfolioId)
                .Select(candidate => candidate.WorkOrderId)
                .SingleOrDefaultAsync(ct);
            await WorkOrderProgressionLock.AcquireAsync(context, ct, linkedWorkOrderId);
            await context.AcquireLockAsync("Conversation", conversationId, ct);
            var conversationQuery = _db.Set<Conversation>()
                .Include(candidate => candidate.Tenant)
                .Where(candidate => candidate.Id == conversationId
                    && candidate.PortfolioId == command.PortfolioId
                    && (command.SenderRole != ConversationSenderRole.Tenant
                        || candidate.TenantId == command.TenantId));
            if (command.SenderRole == ConversationSenderRole.Landlord && command.ManagementAccess is not null)
            {
                var now = await context.ReadDatabaseClockUtcAsync(ct);
                conversationQuery = WhereManagementAuthorized(
                    conversationQuery, _db, command, now);
            }

            var existing = await conversationQuery.SingleOrDefaultAsync(ct);
            if (existing?.Tenant is null)
            {
                return NotFound();
            }

            if (existing.WorkOrderId is int workOrderId &&
                !await _db.Set<WorkOrder>().AsNoTracking().AnyAsync(workOrder =>
                    workOrder.Id == workOrderId &&
                    workOrder.PortfolioId == command.PortfolioId &&
                    workOrder.Status != WorkOrderStatus.Cancelled &&
                    workOrder.Status != WorkOrderStatus.Archived, ct))
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
            var tenantQuery = _db.Set<Tenant>()
                .Where(candidate => candidate.Id == command.TenantId
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.DeletedAt == null);
            if (command.SenderRole == ConversationSenderRole.Landlord && command.ManagementAccess is not null)
            {
                var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
                tenantQuery = WhereManagementAuthorizedForStart(
                    tenantQuery, _db, command, times.WallClockUtc, times.BusinessDate);
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
            _db.Add(conversation);
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
        _db.Add(message);
        await context.FlushBusinessAsync(ct);

        context.StageSemanticEvent(new AtomicSemanticAudit(
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
        context.StageSemanticEvent(new AtomicSemanticAudit(
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
            StageDestinationIntents(context, command, tenant, conversation, message, channels);
        }
        var notifications = command.SenderRole == ConversationSenderRole.Landlord
            ? await CreatePortalNotificationsAsync(
                context,
                _db,
                command,
                conversation,
                channels,
                DateOnly.FromDateTime(command.CreatedAtUtc),
                ct)
            : await CreateStaffNotificationsAsync(context, _db, command, conversation, tenant, ct);
        if (notifications.Count > 0)
        {
            _db.AddRange(notifications);
            await context.FlushBusinessAsync(ct);
            foreach (var notification in notifications)
            {
                context.StageSemanticEvent(new AtomicSemanticAudit(
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
        SendConversationMessageCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        if (command.SenderRole != ConversationSenderRole.Landlord || command.ManagementAccess is null)
        {
            return;
        }

        bool authorized;
        if (command.ConversationId is { } conversationId)
        {
            var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            authorized = await WhereManagementAuthorized(
                    _db.Set<Conversation>().Where(conversation =>
                        conversation.Id == conversationId &&
                        conversation.PortfolioId == command.PortfolioId),
                    _db,
                    command,
                    now)
                .AnyAsync(ct);
        }
        else
        {
            var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
            authorized = await WhereManagementAuthorizedForStart(
                    _db.Set<Tenant>().Where(tenant =>
                        tenant.Id == command.TenantId &&
                        tenant.PortfolioId == command.PortfolioId &&
                        tenant.DeletedAt == null),
                    _db,
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

    internal static IQueryable<Conversation> WhereManagementAuthorized(
        IQueryable<Conversation> conversations,
        RentalCommandDbContext db,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var allProperties = AuthorizedAssignments(db, command, utcNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var authorizedProperties = AuthorizedProperties(db, command, utcNow);

        return conversations.Where(conversation =>
            (conversation.PropertyId == null && allProperties.Any()) ||
            (conversation.PropertyId != null && authorizedProperties.Any(property =>
                property.Id == conversation.PropertyId &&
                property.PortfolioId == conversation.PortfolioId)));
    }

    internal static IQueryable<Tenant> WhereManagementAuthorizedForStart(
        IQueryable<Tenant> tenants,
        RentalCommandDbContext db,
        SendConversationMessageCommand command,
        DateTime utcNow,
        DateOnly businessDate)
    {
        var currentRelationships = db.Set<LeaseManagementParty>()
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
            var authorizedProperties = AuthorizedProperties(db, command, utcNow)
                .Where(property => property.Id == propertyId);
            return tenants.Where(tenant =>
                authorizedProperties.Any() &&
                currentRelationships.Any(party =>
                    party.TenantId == tenant.Id &&
                    party.LeaseManagement!.PropertyId == propertyId));
        }

        var allProperties = AuthorizedAssignments(db, command, utcNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        return tenants.Where(tenant =>
            allProperties.Any() &&
            !currentRelationships.Any(party => party.TenantId == tenant.Id));
    }

    internal static IQueryable<Property> AuthorizedProperties(
        RentalCommandDbContext db,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var assignments = AuthorizedAssignments(db, command, utcNow);
        return db.Set<Property>().Where(property =>
            property.PortfolioId == command.PortfolioId &&
            assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                 assignment.SelectedProperties.Any(selected =>
                     selected.PortfolioId == property.PortfolioId &&
                     selected.PropertyId == property.Id))));
    }

    internal static IQueryable<MembershipRoleAssignment> AuthorizedAssignments(
        RentalCommandDbContext db,
        SendConversationMessageCommand command,
        DateTime utcNow)
    {
        var access = command.ManagementAccess
            ?? throw new InvalidOperationException("Management access is required for this query.");
        if (access.RequiredCapabilityKey is { } requiredCapability &&
            !ManagementCapabilities.Contains(requiredCapability, StringComparer.Ordinal))
        {
            return db.Set<MembershipRoleAssignment>().Where(_ => false);
        }

        IReadOnlyCollection<string> capabilityKeys = access.RequiredCapabilityKey is null
            ? ManagementCapabilities
            : [access.RequiredCapabilityKey];
        var assignments = db.AuthorizedAssignmentsForScope(
            new WorkspaceReadScope(
                command.PortfolioId,
                access.UserId,
                access.SessionId,
                access.AccessContextId,
                access.AccessRevision),
            capabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            utcNow);
        return access.RequiredCapabilityKey is null
            ? assignments
            : assignments.Where(assignment =>
                assignment.RoleProfile != null &&
                assignment.RoleProfile.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition != null &&
                    profileCapability.CapabilityDefinition.Key == access.RequiredCapabilityKey));
    }

    private static async Task<List<Notification>> CreateStaffNotificationsAsync(
        IAtomicCommandContext context,
        RentalCommandDbContext db,
        SendConversationMessageCommand command,
        Conversation conversation,
        Tenant tenant,
        CancellationToken ct)
    {
        // A tenant message resolves the current relationship property and the saved leasing topic
        // responsibility in one query. Named recipients are revalidated at event time and the
        // visible administrator fallback applies only when nobody eligible is assigned.
        var userIds = ScopedNotificationRecipientQuery
            .ForTenantTeamTopic(
                db,
                command.PortfolioId,
                tenant.Id,
                TeamRoutingTopic.ApplicationsAndLeasing,
                command.CreatedAtUtc);
        var recipients = await (
                from accessContext in db.Set<WorkspaceAccessContext>()
                join membership in db.Set<WorkspaceMembership>()
                    on new { AccessContextId = accessContext.Id, accessContext.PortfolioId }
                    equals new { membership.AccessContextId, membership.PortfolioId }
                where accessContext.PortfolioId == command.PortfolioId
                    && userIds.Contains(accessContext.UserId)
                select new
                {
                    accessContext.UserId,
                    AccessContextId = accessContext.Id,
                    accessContext.AccessRevision,
                    Experience = accessContext.LastAuthorizedExperience ?? membership.DefaultExperience,
                })
            .Distinct()
            .OrderBy(recipient => recipient.UserId)
            .ToListAsync(ct);

        var tenantName = $"{tenant.FirstName} {tenant.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(tenantName)) tenantName = "Tenant";
        return recipients.Select(recipient =>
        {
            var notification = new Notification
            {
                PortfolioId = command.PortfolioId,
                UserId = recipient.UserId,
                Type = "TenantMessage",
                Title = $"New message from {tenantName}",
                Message = Preview(command.Body) ?? conversation.Subject,
                Severity = "Info",
                RelatedEntityType = nameof(Conversation),
                RelatedEntityId = conversation.Id,
                CreatedAt = command.CreatedAtUtc,
            };
            if (recipient.Experience is WorkspaceExperience.Management or WorkspaceExperience.Leasing)
            {
                notification.NavigationExperience =
                    (NavigationExperience)(int)recipient.Experience;
                notification.NavigationDestination = NavigationDestination.Message;
                notification.NavigationAccessContextId = recipient.AccessContextId;
                notification.NavigationAccessRevision = recipient.AccessRevision;
                notification.NavigationResourceKind = nameof(Conversation);
                notification.NavigationResourceId = conversation.Id;
                notification.NavigationAction = NavigationAction.Open;
                notification.NavigationExpiresAtUtc = command.CreatedAtUtc.AddDays(7);
                notification.NavigationFallbackDestination = NavigationDestination.Home;
            }
            return notification;
        }).ToList();
    }

    private static async Task<List<Notification>> CreatePortalNotificationsAsync(
        IAtomicCommandContext context,
        RentalCommandDbContext db,
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

        var tenantRecipient = await db.Set<TenantUserAccess>()
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
            .Select(access => new
            {
                UserId = access.ApplicationUserId,
                AccessContextId = access.AccessContextId,
                access.AccessContext!.AccessRevision,
            })
            .FirstOrDefaultAsync(ct);
        if (tenantRecipient is null)
        {
            return [];
        }

        return
        [
            new Notification
            {
                PortfolioId = command.PortfolioId,
                UserId = tenantRecipient.UserId,
                Type = "TenantNotice",
                Title = conversation.Subject,
                Message = Preview(command.Body) ?? conversation.Subject,
                Severity = "Info",
                NavigationExperience = NavigationExperience.Tenant,
                NavigationDestination = NavigationDestination.Message,
                NavigationAccessContextId = tenantRecipient.AccessContextId,
                NavigationAccessRevision = tenantRecipient.AccessRevision,
                NavigationResourceKind = nameof(Conversation),
                NavigationResourceId = conversation.Id,
                NavigationAction = NavigationAction.Open,
                NavigationExpiresAtUtc = command.CreatedAtUtc.AddDays(7),
                NavigationFallbackDestination = NavigationDestination.Home,
                RelatedEntityType = nameof(Conversation),
                RelatedEntityId = conversation.Id,
                CreatedAt = command.CreatedAtUtc,
            },
        ];
    }

    private static void StageDestinationIntents(
        IAtomicCommandContext context,
        SendConversationMessageCommand command,
        Tenant tenant,
        Conversation conversation,
        ConversationMessage message,
        IReadOnlyCollection<string> channels)
    {
        if (channels.Contains("Email", StringComparer.Ordinal))
        {
            context.StageOutbox(new OutboxMessage
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
            context.StageOutbox(new OutboxMessage
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
