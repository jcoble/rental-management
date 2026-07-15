using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public sealed record AtomicNoticeDeliveryCommand(
    int PortfolioId,
    int? ActorUserId,
    Guid? AuthSessionId,
    int? AccessContextId,
    long? ExpectedAccessRevision,
    int NoticeDraftId,
    NoticeDeliveryChannel[] Channels,
    long? WorkItemId,
    Guid? WorkClaimToken,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicNoticeDeliveryResult(
    long RenderedNoticeId,
    int NoticeDraftId,
    int DeliveryCount) : IAtomicResultData;

/// <summary>
/// Freezes one approved notice and its exact destination fan-out under one receipt. The recipient
/// projection is one translated SQL query; the resulting immutable evidence/outbox graph, canonical
/// tenant-inbox messages, draft transition, and fenced work completion share the kernel-owned transaction.
/// </summary>
public sealed class AtomicNoticeDeliveryHandler
    : IAtomicCommandHandler<AtomicNoticeDeliveryCommand, AtomicNoticeDeliveryResult>,
      IAtomicReplayAuthorizer<AtomicNoticeDeliveryCommand>
{
    public async Task<AtomicNoticeDeliveryResult> HandleAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.NoticeDraft, command.NoticeDraftId, ct);
        if (command.ActorUserId is not null)
        {
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.AuthSession, command.AuthSessionId!.Value, ct);
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.WorkspaceAccessContext, command.AccessContextId!.Value, ct);
        }

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeCallerAsync(command, attempt.Persistence, now, requireActiveClaim: true, ct);

        var draftQuery = attempt.Persistence.Query<NoticeDraft>().Where(draft =>
            draft.Id == command.NoticeDraftId
            && draft.PortfolioId == command.PortfolioId
            && draft.Status == "Draft");
        if (command.ActorUserId is not null)
        {
            var authorizedProperties = AuthorizedProperties(command, attempt.Persistence, now);
            draftQuery = draftQuery.Where(draft =>
                draft.PropertyId != null
                && authorizedProperties.Any(property =>
                    property.Id == draft.PropertyId.Value
                    && property.PortfolioId == draft.PortfolioId));
        }

        var draft = await draftQuery.SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Draft does not exist or is no longer editable.");

        var foundation = await (
            from policy in attempt.Persistence.Query<TenantNoticePolicy>().AsNoTracking()
            join template in attempt.Persistence.Query<WorkspaceNoticeTemplateVersion>().AsNoTracking()
                on new
                {
                    TemplateId = draft.WorkspaceNoticeTemplateVersionId
                        ?? policy.WorkspaceNoticeTemplateVersionId,
                    policy.PortfolioId,
                }
                equals new { TemplateId = template.Id, template.PortfolioId }
            join systemTemplate in attempt.Persistence.Query<SystemNoticeTemplateVersion>().AsNoTracking()
                on template.BasedOnSystemTemplateVersionId equals systemTemplate.Id
            where policy.Id == draft.TenantNoticePolicyId
                && policy.PortfolioId == command.PortfolioId
            select new DeliveryFoundation(
                policy.Id,
                policy.Mode,
                policy.Classification,
                policy.SendTenantPortal,
                policy.SendMobilePush,
                policy.SendEmail,
                policy.SendSms,
                policy.IncludePrimaryTenant,
                policy.IncludeCoTenant,
                policy.IncludeEligibleGuarantor,
                policy.IncludeOccupant,
                policy.Mode == TenantNoticeMode.Auto
                    && (policy.Classification != NoticeClassification.Legal
                        || (policy.ReviewedJurisdictionCode != null
                            && policy.ReviewedJurisdictionCode != ""
                            && policy.JurisdictionReviewedAtUtc != null)),
                policy.ReviewedJurisdictionCode,
                template.Id,
                template.SystemKey,
                template.Version,
                template.JurisdictionCode,
                template.JurisdictionReviewedAtUtc,
                systemTemplate.Version))
            .TagWith("TSK-668 immutable tenant notice delivery foundation")
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Draft has no active tenant notice policy and immutable template version.");

        if (foundation.Mode == TenantNoticeMode.Off)
            throw new InvalidOperationException("A disabled policy cannot deliver a notice.");
        if (foundation.Mode == TenantNoticeMode.Auto
            && foundation.Classification == NoticeClassification.Legal
            && (!foundation.CanAutoSend
                || foundation.TemplateJurisdictionReviewedAtUtc is null
                || NormalizeJurisdiction(foundation.ReviewedJurisdictionCode) is not { } policyJurisdiction
                || NormalizeJurisdiction(foundation.TemplateJurisdictionCode) != policyJurisdiction))
        {
            throw new InvalidOperationException(
                "Legal Auto delivery requires reviewed jurisdiction and template facts.");
        }

        var rendered = new RenderedNotice
        {
            PortfolioId = command.PortfolioId,
            NoticeDraftId = draft.Id,
            WorkspaceNoticeTemplateVersionId = foundation.TemplateId,
            LeaseManagementId = draft.LeaseManagementId,
            Subject = draft.Subject,
            Body = draft.Body,
            ContentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(draft.Subject + "\n" + draft.Body)))
                .ToLowerInvariant(),
            TemplateProvenance = $"{foundation.SystemKey}:workspace-v{foundation.TemplateVersion}:system-v{foundation.SystemTemplateVersion}",
            JurisdictionCode = foundation.TemplateJurisdictionCode,
            RenderedAtUtc = now,
            ApprovedByUserId = command.ActorUserId,
            ApprovedAtUtc = now,
        };
        attempt.Persistence.Add(rendered);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(
            command, nameof(RenderedNotice), checked((int)rendered.Id), AuditLogOperation.Created,
            "Tenant notice content and template provenance frozen for delivery"), now);

        var eligibleParties =
            from party in attempt.Persistence.Query<LeaseManagementParty>().AsNoTracking()
            join tenant in attempt.Persistence.Query<Tenant>().AsNoTracking()
                on new { party.TenantId, party.PortfolioId }
                equals new { TenantId = tenant.Id, tenant.PortfolioId }
            join management in attempt.Persistence.Query<LeaseManagement>().AsNoTracking()
                on new { LeaseManagementId = party.LeaseManagementId, party.PortfolioId }
                equals new { LeaseManagementId = management.Id, management.PortfolioId }
            join lifecycle in attempt.Persistence.Query<LeaseManagementLifecycleProjection>().AsNoTracking()
                on new { LeaseManagementId = management.Id, management.PortfolioId }
                equals new { LeaseManagementId = lifecycle.LeaseManagementId, lifecycle.PortfolioId }
            where party.PortfolioId == command.PortfolioId
                && party.LeaseManagementId == draft.LeaseManagementId
                && party.Id == draft.RecipientLeaseManagementPartyId
                && tenant.DeletedAt == null
                && lifecycle.TenantAccountId == draft.TenantAccountId
                && !lifecycle.HasReconciliationException
                && party.EffectiveFrom <= lifecycle.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
                && ((party.Role == LeaseManagementPartyRole.PrimaryTenant && foundation.IncludePrimaryTenant)
                    || (party.Role == LeaseManagementPartyRole.CoTenant && foundation.IncludeCoTenant)
                    || (party.Role == LeaseManagementPartyRole.Guarantor
                        && foundation.IncludeEligibleGuarantor
                        && (foundation.Classification != NoticeClassification.Legal
                            || party.GuarantorLegalNoticeEligible))
                    || (party.Role == LeaseManagementPartyRole.Occupant
                        && foundation.IncludeOccupant
                        && foundation.Classification != NoticeClassification.Legal
                        && foundation.SystemKey != "rent-reminder"
                        && foundation.SystemKey != "late-rent-late-fee"))
            select new
            {
                TenantId = tenant.Id,
                LeaseManagementPartyId = party.Id,
                party.Role,
                tenant.Email,
                tenant.Phone,
                management.PropertyId,
                management.UnitId,
            };

        var portal =
            from party in eligibleParties
            join access in attempt.Persistence.Query<EffectiveTenantAccessProjection>().AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = command.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            where command.Channels.Contains(NoticeDeliveryChannel.TenantPortal)
                && foundation.SendTenantPortal
                && access.UserId == attempt.Persistence.Query<EffectiveTenantAccessProjection>()
                    .Where(candidate => candidate.PortfolioId == command.PortfolioId
                        && candidate.LeaseManagementPartyId == party.LeaseManagementPartyId)
                    .OrderBy(candidate => candidate.UserId)
                    .Select(candidate => candidate.UserId)
                    .First()
            select new
            {
                party.LeaseManagementPartyId,
                party.TenantId,
                PartyRole = party.Role,
                Channel = NoticeDeliveryChannel.TenantPortal,
                Destination = access.UserId.ToString(),
                RecipientUserId = (int?)access.UserId,
                party.PropertyId,
                party.UnitId,
            };
        var email = eligibleParties
            .Where(row => command.Channels.Contains(NoticeDeliveryChannel.Email)
                && foundation.SendEmail && row.Email != null && row.Email != "")
            .Select(row => new
            {
                row.LeaseManagementPartyId,
                row.TenantId,
                PartyRole = row.Role,
                Channel = NoticeDeliveryChannel.Email,
                Destination = row.Email!,
                RecipientUserId = (int?)null,
                row.PropertyId,
                row.UnitId,
            });
        var sms = eligibleParties
            .Where(row => command.Channels.Contains(NoticeDeliveryChannel.Sms)
                && foundation.SendSms && row.Phone != null && row.Phone != "")
            .Select(row => new
            {
                row.LeaseManagementPartyId,
                row.TenantId,
                PartyRole = row.Role,
                Channel = NoticeDeliveryChannel.Sms,
                Destination = row.Phone!,
                RecipientUserId = (int?)null,
                row.PropertyId,
                row.UnitId,
            });
        var push =
            from party in eligibleParties
            join access in attempt.Persistence.Query<EffectiveTenantAccessProjection>().AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = command.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            join device in attempt.Persistence.Query<DeviceToken>().AsNoTracking()
                on access.UserId equals device.UserId
            where command.Channels.Contains(NoticeDeliveryChannel.MobilePush)
                && foundation.SendMobilePush
                && device.PortfolioId == command.PortfolioId
                && device.Id == attempt.Persistence.Query<DeviceToken>()
                    .Where(candidate => candidate.PortfolioId == command.PortfolioId
                        && candidate.UserId == access.UserId)
                    .OrderByDescending(candidate => candidate.LastSeenAt)
                    .ThenByDescending(candidate => candidate.Id)
                    .Select(candidate => candidate.Id)
                    .First()
            select new
            {
                party.LeaseManagementPartyId,
                party.TenantId,
                PartyRole = party.Role,
                Channel = NoticeDeliveryChannel.MobilePush,
                Destination = device.Token,
                RecipientUserId = (int?)access.UserId,
                party.PropertyId,
                party.UnitId,
            };

        var destinationRows = await portal.Union(email).Union(sms).Union(push)
            .OrderBy(row => row.TenantId)
            .ThenBy(row => row.Channel)
            .ThenBy(row => row.Destination)
            .TagWith("TSK-668 exact effective tenant notice recipients and destinations")
            .ToListAsync(ct);
        var destinations = destinationRows.Select(row => new DeliveryProjection(
            row.LeaseManagementPartyId,
            row.TenantId,
            row.PartyRole,
            row.Channel,
            row.Destination,
            row.RecipientUserId,
            row.PropertyId,
            row.UnitId)).ToList();
        if (destinations.Count == 0)
        {
            throw new InvalidOperationException(
                "No eligible recipient has a configured destination for the selected channels.");
        }

        var portalMessages = new Dictionary<int, ConversationMessage>();
        foreach (var destination in destinations)
        {
            if (destination.Channel != NoticeDeliveryChannel.TenantPortal) continue;
            var conversation = new Conversation
            {
                PortfolioId = command.PortfolioId,
                TenantId = destination.TenantId,
                Subject = rendered.Subject,
                PropertyId = destination.PropertyId,
                StartedByLandlord = true,
                CreatedAt = now,
                LastMessageAt = now,
                LastMessagePreview = rendered.Body.Length <= 280 ? rendered.Body : rendered.Body[..280],
                TenantUnreadCount = 1,
            };
            var message = new ConversationMessage
            {
                Conversation = conversation,
                SenderRole = ConversationSenderRole.Landlord,
                Body = rendered.Body,
                Channels = "Portal",
                CreatedAt = now,
            };
            attempt.Persistence.Add(message);
            portalMessages.Add(destination.LeaseManagementPartyId, message);
        }
        if (portalMessages.Count > 0)
        {
            await attempt.FlushBusinessAsync(ct);
        }
        if (portalMessages.TryGetValue(draft.RecipientLeaseManagementPartyId, out var recipientMessage))
        {
            draft.ConversationId = recipientMessage.ConversationId;
        }

        var evidenceRows = new List<NoticeDeliveryEvidence>(destinations.Count);
        var portalNotifications = new List<Notification>(portalMessages.Count);
        foreach (var destination in destinations)
        {
            var destinationHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(destination.Destination)))
                .ToLowerInvariant()[..16];
            var deliveryKey = $"notice:{rendered.Id}:party:{destination.LeaseManagementPartyId}:{destination.Channel}:{destinationHash}";
            ConversationMessage? portalMessage = null;
            if (destination.Channel == NoticeDeliveryChannel.TenantPortal)
            {
                portalMessage = portalMessages[destination.LeaseManagementPartyId];
                var notification = new Notification
                {
                    PortfolioId = command.PortfolioId,
                    UserId = destination.RecipientUserId
                        ?? throw new InvalidOperationException("Portal delivery requires an effective tenant user."),
                    Type = "TenantNotice",
                    Title = rendered.Subject,
                    Message = rendered.Body.Length <= 280 ? rendered.Body : rendered.Body[..280],
                    Severity = "Info",
                    ActionUrl = $"/portal/messages?conversation={portalMessage.ConversationId}",
                    RelatedEntityType = nameof(Conversation),
                    RelatedEntityId = portalMessage.ConversationId,
                    CreatedAt = now,
                };
                attempt.Persistence.Add(notification);
                portalNotifications.Add(notification);
            }
            var (messageType, payload) = Payload(rendered, draft, destination, portalMessage);
            var outbox = new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = messageType,
                Payload = payload,
                IdempotencyKey = deliveryKey,
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            };

            var evidence = new NoticeDeliveryEvidence
            {
                PortfolioId = command.PortfolioId,
                RenderedNoticeId = rendered.Id,
                RecipientLeaseManagementPartyId = destination.LeaseManagementPartyId,
                RecipientRole = RecipientRole(destination.PartyRole),
                Channel = destination.Channel,
                Destination = destination.Destination,
                OutboxMessage = outbox,
                ConversationMessage = portalMessage,
                IdempotencyKey = deliveryKey,
                CreatedAtUtc = now,
            };
            attempt.Persistence.Add(evidence);
            evidenceRows.Add(evidence);
        }

        draft.Status = "Approved";
        draft.ApprovedAt = now;
        draft.ApprovedChannels = string.Join(",", command.Channels);
        draft.RenderedNoticeId = rendered.Id;
        draft.UpdatedAt = now;
        TenantNoticeWorkItem? completedWorkItem = null;
        if (command.WorkItemId is not null)
        {
            var workItem = await attempt.Persistence.Query<TenantNoticeWorkItem>()
                .SingleOrDefaultAsync(row =>
                    row.Id == command.WorkItemId.Value
                    && row.PortfolioId == command.PortfolioId
                    && row.LeaseManagementId == draft.LeaseManagementId
                    && row.Status == TenantNoticeWorkStatus.Claimed
                    && row.ClaimToken == command.WorkClaimToken, ct)
                ?? throw new DbUpdateConcurrencyException(
                    $"Tenant notice work item {command.WorkItemId} is no longer owned by this claim.");
            workItem.Status = TenantNoticeWorkStatus.Completed;
            workItem.ClaimOwner = null;
            workItem.ClaimToken = null;
            workItem.ClaimExpiresAtUtc = null;
            completedWorkItem = workItem;
        }

        await attempt.FlushBusinessAsync(ct);
        foreach (var portalMessage in portalMessages.Values)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Conversation), portalMessage.ConversationId, AuditLogOperation.Created,
                "Approved tenant notice opened a canonical tenant conversation"), now);
            attempt.StageSemanticEvent(Audit(
                command, nameof(ConversationMessage), portalMessage.Id, AuditLogOperation.Created,
                "Approved tenant notice committed to the canonical tenant inbox"), now);
        }
        foreach (var evidence in evidenceRows)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(NoticeDeliveryEvidence), checked((int)evidence.Id),
                AuditLogOperation.Created, $"Tenant notice queued for {evidence.Channel}"), now);
        }
        foreach (var notification in portalNotifications)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Notification), notification.Id, AuditLogOperation.Created,
                "Approved tenant notice added to the recipient notification inbox"), now);
        }
        attempt.StageSemanticEvent(Audit(
            command, nameof(NoticeDraft), draft.Id, AuditLogOperation.Updated,
            "Tenant notice approved and queued for delivery"), now);
        if (completedWorkItem is not null)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(TenantNoticeWorkItem), checked((int)completedWorkItem.Id),
                AuditLogOperation.Updated, "Tenant notice automation work completed"), now);
        }
        return new AtomicNoticeDeliveryResult(rendered.Id, draft.Id, destinations.Count);
    }

    public async Task AuthorizeReplayAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeCallerAsync(command, persistence, now, requireActiveClaim: false, ct);
    }

    private static async Task AuthorizeCallerAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        bool requireActiveClaim,
        CancellationToken ct)
    {
        if (command.ActorUserId is not null)
        {
            if (!await IdentityAuthorized(command, persistence, now).AnyAsync(ct))
                throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
            if (!await persistence.Query<NoticeDraft>().AsNoTracking().AnyAsync(draft =>
                    draft.Id == command.NoticeDraftId
                    && draft.PortfolioId == command.PortfolioId
                    && draft.PropertyId != null
                    && AuthorizedProperties(command, persistence, now).Any(property =>
                        property.Id == draft.PropertyId.Value
                        && property.PortfolioId == draft.PortfolioId), ct))
            {
                throw new UnauthorizedAccessException(
                    "The current Team role cannot manage this tenant notice.");
            }
            return;
        }

        var workAuthorized = await persistence.Query<TenantNoticeWorkItem>().AsNoTracking().AnyAsync(row =>
            row.Id == command.WorkItemId
            && row.PortfolioId == command.PortfolioId
            && (requireActiveClaim
                ? row.Status == TenantNoticeWorkStatus.Claimed
                    && row.ClaimToken == command.WorkClaimToken
                : row.Status == TenantNoticeWorkStatus.Completed
                    || row.Status == TenantNoticeWorkStatus.Claimed
                    && row.ClaimToken == command.WorkClaimToken), ct);
        if (!workAuthorized)
        {
            if (requireActiveClaim)
            {
                throw new DbUpdateConcurrencyException(
                    $"Tenant notice work item {command.WorkItemId} is no longer owned by this claim.");
            }
            throw new UnauthorizedAccessException("Tenant notice automation claim is no longer valid.");
        }
    }

    private static IQueryable<Property> AuthorizedProperties(
        AtomicNoticeDeliveryCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.TenantNoticesManage
                && grant.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
        return persistence.Query<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId
            && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                && assignment.SelectedProperties.Any(selected =>
                    selected.PortfolioId == property.PortfolioId
                    && selected.PropertyId == property.Id)));
    }

    private static IQueryable<WorkspaceAccessContext> IdentityAuthorized(
        AtomicNoticeDeliveryCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now) =>
        persistence.Query<WorkspaceAccessContext>().AsNoTracking().Where(context =>
            context.Id == command.AccessContextId
            && context.UserId == command.ActorUserId
            && context.PortfolioId == command.PortfolioId
            && context.AccessRevision == command.ExpectedAccessRevision
            && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null
            && context.RevokedAtUtc == null
            && persistence.Query<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now));

    private static (string MessageType, string Payload) Payload(
        RenderedNotice rendered,
        NoticeDraft draft,
        DeliveryProjection destination,
        ConversationMessage? portalMessage) => destination.Channel switch
    {
        NoticeDeliveryChannel.Email => ("email", JsonSerializer.Serialize(new
        {
            to = destination.Destination,
            rendered.Subject,
            body = rendered.Body,
            renderedNoticeId = rendered.Id,
        })),
        NoticeDeliveryChannel.Sms => ("sms", JsonSerializer.Serialize(new
        {
            to = destination.Destination,
            message = rendered.Body,
            renderedNoticeId = rendered.Id,
        })),
        NoticeDeliveryChannel.MobilePush => ("push", JsonSerializer.Serialize(new
        {
            deviceToken = destination.Destination,
            title = rendered.Subject,
            body = rendered.Body,
            actionUrl = "/notices",
            type = "TenantNotice",
            relatedEntityType = nameof(RenderedNotice),
            relatedEntityId = rendered.Id.ToString(),
        })),
        NoticeDeliveryChannel.TenantPortal when portalMessage is not null =>
            ("data-update", JsonSerializer.Serialize(new
            {
                entityType = nameof(Conversation),
                entityId = portalMessage.ConversationId,
                operation = "create",
                data = new
                {
                    renderedNoticeId = rendered.Id,
                    noticeDraftId = draft.Id,
                    tenantId = destination.TenantId,
                    leaseManagementId = rendered.LeaseManagementId,
                    conversationId = portalMessage.ConversationId,
                    messageId = portalMessage.Id,
                },
            })),
        _ => throw new InvalidOperationException(
            $"Unsupported tenant notice channel {destination.Channel}."),
    };

    private static NoticeRecipientRole RecipientRole(LeaseManagementPartyRole role) => role switch
    {
        LeaseManagementPartyRole.PrimaryTenant => NoticeRecipientRole.PrimaryTenant,
        LeaseManagementPartyRole.CoTenant => NoticeRecipientRole.CoTenant,
        LeaseManagementPartyRole.Guarantor => NoticeRecipientRole.Guarantor,
        LeaseManagementPartyRole.Occupant => NoticeRecipientRole.Occupant,
        _ => throw new InvalidOperationException($"Unsupported tenant notice recipient role {role}."),
    };

    private static AtomicSemanticAudit Audit(
        AtomicNoticeDeliveryCommand command,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        string reason) =>
        new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private static string? NormalizeJurisdiction(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static void Validate(AtomicNoticeDeliveryCommand command)
    {
        var manual = command.ActorUserId is not null;
        if (command.PortfolioId <= 0
            || command.NoticeDraftId <= 0
            || command.Channels.Length == 0
            || command.Channels.Distinct().Count() != command.Channels.Length
            || command.Channels.Any(channel => !Enum.IsDefined(channel))
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128
            || (manual && (command.ActorUserId.GetValueOrDefault() <= 0
                || command.AuthSessionId.GetValueOrDefault() == Guid.Empty
                || command.AccessContextId.GetValueOrDefault() <= 0
                || command.ExpectedAccessRevision.GetValueOrDefault() <= 0
                || command.WorkItemId is not null
                || command.WorkClaimToken is not null))
            || (!manual && (command.AuthSessionId is not null
                || command.AccessContextId is not null
                || command.ExpectedAccessRevision is not null
                || command.WorkItemId.GetValueOrDefault() <= 0
                || command.WorkClaimToken.GetValueOrDefault() == Guid.Empty)))
        {
            throw new ArgumentException(
                "Tenant notice delivery requires one valid workspace caller or one fenced automation claim.");
        }
    }

    private sealed record DeliveryFoundation(
        int PolicyId,
        TenantNoticeMode Mode,
        NoticeClassification Classification,
        bool SendTenantPortal,
        bool SendMobilePush,
        bool SendEmail,
        bool SendSms,
        bool IncludePrimaryTenant,
        bool IncludeCoTenant,
        bool IncludeEligibleGuarantor,
        bool IncludeOccupant,
        bool CanAutoSend,
        string? ReviewedJurisdictionCode,
        int TemplateId,
        string SystemKey,
        int TemplateVersion,
        string? TemplateJurisdictionCode,
        DateTime? TemplateJurisdictionReviewedAtUtc,
        int SystemTemplateVersion);

    private sealed record DeliveryProjection(
        int LeaseManagementPartyId,
        int TenantId,
        LeaseManagementPartyRole PartyRole,
        NoticeDeliveryChannel Channel,
        string Destination,
        int? RecipientUserId,
        int PropertyId,
        int UnitId);
}

public static class AtomicNoticeDelivery
{
    public static readonly AtomicJsonResultCodec<AtomicNoticeDeliveryResult> Codec =
        new("rental.notice-delivery.v1");

    public static AtomicNoticeDeliveryCommand Command(
        NoticeApprovalExecutionContext context,
        int draftId,
        IReadOnlyList<NoticeDeliveryChannel> channels,
        TenantNoticeWorkFence? workFence,
        string operationKey) =>
        new(
            context.PortfolioId,
            context.ActorUserId,
            context.AuthSessionId,
            context.AccessContextId,
            context.ExpectedAccessRevision,
            draftId,
            channels.Distinct().OrderBy(channel => channel).ToArray(),
            workFence?.WorkItemId,
            workFence?.ClaimToken,
            operationKey);

    public static AtomicCommandIdentity Identity(AtomicNoticeDeliveryCommand command) =>
        new("rental.notice-delivery.approve",
            $"{command.PortfolioId}:{command.NoticeDraftId}:{command.DeliveryIdempotencyKey}");
}
