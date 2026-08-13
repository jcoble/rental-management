using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Notifications;
using RentalCommand.Data.Notifications;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Api.Services;

namespace RentalCommand.Api.Services.Domain;

public sealed record AtomicNoticeDeliveryCommand(
    int PortfolioId,
    int? ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid? AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int? AccessContextId,
    [property: AtomicFingerprintIgnore]
    long? ExpectedAccessRevision,
    int NoticeDraftId,
    NoticeDeliveryChannel[] Channels,
    long? WorkItemId,
    Guid? WorkClaimToken,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicNoticeDeliveryResult(
    long RenderedNoticeId,
    int NoticeDraftId,
    int DeliveryCount);

public sealed class NoticeApprovalAuthorizationException : UnauthorizedAccessException
{
    public NoticeApprovalAuthorizationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Freezes one approved notice and its exact destination fan-out under one receipt. The recipient
/// projection is one translated SQL query; the resulting immutable evidence/outbox graph, canonical
/// tenant-inbox messages, draft transition, and fenced work completion share the kernel-owned transaction.
/// </summary>
public sealed class AtomicNoticeDeliveryHandler
    : IAtomicCommandHandler<AtomicNoticeDeliveryCommand, AtomicNoticeDeliveryResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicNoticeDeliveryHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicNoticeDeliveryResult> HandleAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        if (command.ActorUserId is not null)
        {
            await attempt.AcquireLockAsync(
                "AuthSession", command.AuthSessionId!.Value, ct);
            await attempt.AcquireLockAsync(
                "WorkspaceAccessContext", command.AccessContextId!.Value, ct);
        }
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        await attempt.AcquireLockAsync("NoticeDraft", command.NoticeDraftId, ct);

        var times = await AtomicCommandDbClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var securityNow = times.WallClockUtc;
        var now = times.EffectiveNowUtc;
        attempt.UseDatabaseWallClockForAudit(securityNow);
        await AuthorizeCallerAsync(command, _db, securityNow, requireActiveClaim: true, ct);

        var draftQuery = _db.Set<NoticeDraft>().Where(draft =>
            draft.Id == command.NoticeDraftId
            && draft.PortfolioId == command.PortfolioId
            && (draft.Status == "Draft" || draft.Status == "Approved")
            && (draft.TenantLedgerEntryId == null || _db.Set<TenantLedgerEntry>().Any(entry =>
                entry.Id == draft.TenantLedgerEntryId.Value
                && entry.PortfolioId == draft.PortfolioId
                && entry.TenantAccountId == draft.TenantAccountId)));
        if (command.ActorUserId is not null)
        {
            var authorizedProperties = AuthorizedProperties(command, _db, securityNow);
            draftQuery = draftQuery.Where(draft =>
                draft.PropertyId != null
                && authorizedProperties.Any(property =>
                    property.Id == draft.PropertyId.Value
                    && property.PortfolioId == draft.PortfolioId));
        }

        var draft = await draftQuery.SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Draft does not exist or is no longer editable.");

        NoticeDeliveryContentSafety.RequireSafe(draft.Subject, draft.Body);

        var foundation = await (
            from policy in _db.Set<TenantNoticePolicy>().AsNoTracking()
            join template in _db.Set<WorkspaceNoticeTemplateVersion>().AsNoTracking()
                on new
                {
                    TemplateId = draft.WorkspaceNoticeTemplateVersionId
                        ?? policy.WorkspaceNoticeTemplateVersionId,
                    policy.PortfolioId,
                }
                equals new { TemplateId = template.Id, template.PortfolioId }
            join systemTemplate in _db.Set<SystemNoticeTemplateVersion>().AsNoTracking()
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
            ?? throw new InvalidOperationException("Draft has no active tenant notice policy and current template version.");

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

        var eligibleParties =
            from party in _db.Set<LeaseManagementParty>().AsNoTracking()
            join tenant in _db.Set<Tenant>().AsNoTracking()
                on new { party.TenantId, party.PortfolioId }
                equals new { TenantId = tenant.Id, tenant.PortfolioId }
            join management in _db.Set<LeaseManagement>().AsNoTracking()
                on new { LeaseManagementId = party.LeaseManagementId, party.PortfolioId }
                equals new { LeaseManagementId = management.Id, management.PortfolioId }
            join lifecycle in _db.Set<LeaseManagementLifecycleProjection>().AsNoTracking()
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
                        && foundation.Classification == NoticeClassification.Legal
                        && party.GuarantorLegalNoticeEligible)
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
            join access in _db.Set<EffectiveTenantAccessProjection>().AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = command.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            where command.Channels.Contains(NoticeDeliveryChannel.TenantPortal)
                && foundation.SendTenantPortal
                && access.UserId == _db.Set<EffectiveTenantAccessProjection>()
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
                NavigationAccessContextId = (int?)access.AccessContextId,
                NavigationAccessRevision = (long?)access.AccessRevision,
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
                NavigationAccessContextId = (int?)null,
                NavigationAccessRevision = (long?)null,
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
                NavigationAccessContextId = (int?)null,
                NavigationAccessRevision = (long?)null,
                row.PropertyId,
                row.UnitId,
            });
        var push =
            from party in eligibleParties
            join access in _db.Set<EffectiveTenantAccessProjection>().AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = command.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            join device in _db.Set<DeviceToken>().AsNoTracking()
                on access.UserId equals device.UserId
            where command.Channels.Contains(NoticeDeliveryChannel.MobilePush)
                && foundation.SendMobilePush
                && device.PortfolioId == command.PortfolioId
                && device.Id == _db.Set<DeviceToken>()
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
                NavigationAccessContextId = (int?)access.AccessContextId,
                NavigationAccessRevision = (long?)access.AccessRevision,
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
            row.NavigationAccessContextId,
            row.NavigationAccessRevision,
            row.PropertyId,
            row.UnitId)).ToList();
        if (destinations.Count == 0)
        {
            throw new InvalidOperationException(
                "No eligible recipient has a configured destination for the selected channels.");
        }

        var contentHash = ContentHash(draft.Subject, draft.Body);
        var messagePreview = NoticeMessagePreview(draft.Body);
        var templateProvenance =
            $"{foundation.SystemKey}:workspace-v{foundation.TemplateVersion}:system-v{foundation.SystemTemplateVersion}";
        var existingRendered = await _db.Set<RenderedNotice>()
            .SingleOrDefaultAsync(row =>
                row.PortfolioId == command.PortfolioId
                && row.NoticeDraftId == draft.Id, ct);
        if (existingRendered is not null)
        {
            if (draft.Status == "Approved")
            {
                return await ReconcileApprovedDeliveryAsync(
                    command,
                    attempt,
                    draft,
                    existingRendered,
                    destinations,
                    foundation,
                    contentHash,
                    messagePreview,
                    templateProvenance,
                    now,
                    securityNow,
                    ct);
            }

            return await CompleteExistingDeliveryAsync(
                command,
                attempt,
                draft,
                existingRendered,
                destinations,
                foundation,
                contentHash,
                messagePreview,
                templateProvenance,
                now,
                securityNow,
                ct);
        }
        if (draft.Status != "Draft")
        {
            throw new KeyNotFoundException("Draft does not exist or is no longer editable.");
        }

        var rendered = new RenderedNotice
        {
            PortfolioId = command.PortfolioId,
            NoticeDraftId = draft.Id,
            WorkspaceNoticeTemplateVersionId = foundation.TemplateId,
            LeaseManagementId = draft.LeaseManagementId,
            Subject = draft.Subject,
            Body = draft.Body,
            ContentSha256 = contentHash,
            TemplateProvenance = templateProvenance,
            JurisdictionCode = foundation.TemplateJurisdictionCode,
            RenderedAtUtc = now,
            ApprovedByUserId = command.ActorUserId,
            ApprovedAtUtc = now,
        };
        _db.Add(rendered);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(
            command, nameof(RenderedNotice), checked((int)rendered.Id), AuditLogOperation.Created,
            "Tenant notice content and template provenance frozen for delivery"), securityNow);

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
            _db.Add(message);
            portalMessages.Add(destination.LeaseManagementPartyId, message);
        }
        if (portalMessages.Count > 0)
        {
            await attempt.FlushBusinessAsync(ct);
        }
        var recipientConversationId = portalMessages.TryGetValue(
            draft.RecipientLeaseManagementPartyId,
            out var recipientMessage)
            ? recipientMessage.ConversationId
            : (int?)null;

        var evidenceRows = new List<NoticeDeliveryEvidence>(destinations.Count);
        var portalNotifications = new List<Notification>(portalMessages.Count);
        foreach (var destination in destinations)
        {
            var deliveryKey = DeliveryKey(rendered.Id, destination);
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
                    Message = messagePreview,
                    Severity = "Info",
                    NavigationExperience = NavigationExperience.Tenant,
                    NavigationDestination = NoticeNavigationDestination(draft),
                    NavigationAccessContextId = destination.NavigationAccessContextId,
                    NavigationAccessRevision = destination.NavigationAccessRevision,
                    NavigationResourceKind = NoticeNavigationResourceKind(draft),
                    NavigationResourceId = NoticeNavigationResourceId(draft, portalMessage),
                    NavigationParentResourceKind = NoticeNavigationParentResourceKind(draft),
                    NavigationParentResourceId = NoticeNavigationParentResourceId(draft),
                    NavigationAction = NavigationAction.Open,
                    NavigationExpiresAtUtc = now.AddDays(7),
                    NavigationFallbackDestination = NavigationDestination.Home,
                    RelatedEntityType = NoticeRelatedEntityType(draft),
                    RelatedEntityId = NoticeRelatedEntityId(draft, portalMessage),
                    CreatedAt = now,
                };
                _db.Add(notification);
                portalNotifications.Add(notification);
            }
            var (messageType, payload) = Payload(rendered, draft, destination, portalMessage, now);
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
            _db.Add(evidence);
            evidenceRows.Add(evidence);
        }

        TenantNoticeWorkItem? completedWorkItem = null;
        if (command.WorkItemId is not null)
        {
            var workItem = await _db.Set<TenantNoticeWorkItem>()
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

        await AtomicNoticeDraftPersistence.CompleteApprovalAsync(_db,
            attempt,
            command.PortfolioId,
            draft.Id,
            rendered.Id,
            recipientConversationId,
            string.Join(",", command.Channels),
            now,
            now,
            ct);
        await attempt.FlushBusinessAsync(ct);
        var reconciledNotifications = await ReconcileApprovalNotificationIntentAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            messagePreview,
            now,
            ct);
        await RequireApprovalCompletionAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            recipientConversationId,
            string.Join(",", command.Channels),
            messagePreview,
            now,
            ct);
        foreach (var portalMessage in portalMessages.Values)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Conversation), portalMessage.ConversationId, AuditLogOperation.Created,
                "Approved tenant notice opened a tenant conversation"), securityNow);
            attempt.StageSemanticEvent(Audit(
                command, nameof(ConversationMessage), portalMessage.Id, AuditLogOperation.Created,
                "Approved tenant notice was saved to the tenant inbox"), securityNow);
        }
        foreach (var evidence in evidenceRows)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(NoticeDeliveryEvidence), checked((int)evidence.Id),
                AuditLogOperation.Created, $"Tenant notice queued for {evidence.Channel}"), securityNow);
        }
        foreach (var notification in portalNotifications)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Notification), notification.Id, AuditLogOperation.Created,
                "Approved tenant notice added to the recipient notification inbox"), securityNow);
        }
        attempt.StageSemanticEvent(Audit(
            command, nameof(NoticeDraft), draft.Id, AuditLogOperation.Updated,
            "Tenant notice approved and queued for delivery"), securityNow);
        if (reconciledNotifications > 0)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Notification), 0, AuditLogOperation.Updated,
                $"Updated {reconciledNotifications} tenant payment notification(s)"), securityNow);
        }
        if (completedWorkItem is not null)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(TenantNoticeWorkItem), checked((int)completedWorkItem.Id),
                AuditLogOperation.Updated, "Tenant notice automation work completed"), securityNow);
        }
        return new AtomicNoticeDeliveryResult(rendered.Id, draft.Id, destinations.Count);
    }

    private async Task<AtomicNoticeDeliveryResult> ReconcileApprovedDeliveryAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        IReadOnlyList<DeliveryProjection> destinations,
        DeliveryFoundation foundation,
        string contentHash,
        string messagePreview,
        string templateProvenance,
        DateTime now,
        DateTime auditNow,
        CancellationToken ct)
    {
        if (rendered.WorkspaceNoticeTemplateVersionId != foundation.TemplateId
            || rendered.LeaseManagementId != draft.LeaseManagementId
            || rendered.Subject != draft.Subject
            || rendered.Body != draft.Body
            || rendered.ContentSha256 != contentHash
            || rendered.TemplateProvenance != templateProvenance
            || NormalizeJurisdiction(rendered.JurisdictionCode) != NormalizeJurisdiction(foundation.TemplateJurisdictionCode)
            || draft.RenderedNoticeId != rendered.Id)
        {
            throw new InvalidOperationException(
                "Existing rendered notice does not match this approval request.");
        }

        var approvedChannels = string.Join(",", command.Channels);
        if (draft.ApprovedChannels != approvedChannels
            || draft.RenderedNoticeId != rendered.Id
            || draft.ApprovedAt is null
            || rendered.ApprovedAtUtc != draft.ApprovedAt.Value)
        {
            throw new InvalidOperationException(
                "Existing approved notice does not match this approval request.");
        }

        var existingApprovedAt = draft.ApprovedAt.Value;
        var recipientConversationId = draft.ConversationId ?? await ResolveRecipientConversationIdAsync(
            command, attempt, draft, rendered, ct);
        await RequireApprovalRecoveryCandidateAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            recipientConversationId,
            approvedChannels,
            messagePreview,
            existingApprovedAt,
            ct);
        var reconciledNotifications = await ReconcileApprovalNotificationIntentAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            messagePreview,
            existingApprovedAt,
            ct);

        var finalApprovedAt = existingApprovedAt;
        if (existingApprovedAt != now)
        {
            var correctedRows = await AtomicNoticeDraftPersistence.ReconcileApprovedDeliveryChronologyAsync(_db,
                attempt,
                command.PortfolioId,
                draft.Id,
                rendered.Id,
                existingApprovedAt,
                now,
                ct);
            await attempt.FlushBusinessAsync(ct);
            if (correctedRows <= 0)
            {
                throw new InvalidOperationException(
                    "Could not save complete tenant notice delivery details.");
            }

            finalApprovedAt = now;
            attempt.StageSemanticEvent(Audit(
                command, nameof(NoticeDraft), draft.Id, AuditLogOperation.Updated,
                "Corrected tenant notice approval timing after checking delivery records"), auditNow);
        }

        await RequireApprovalCompletionAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            recipientConversationId,
            approvedChannels,
            messagePreview,
            finalApprovedAt,
            ct);

        if (reconciledNotifications > 0)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Notification), 0, AuditLogOperation.Updated,
                $"Updated {reconciledNotifications} tenant payment notification(s)"), auditNow);
        }
        attempt.StageSemanticEvent(Audit(
            command, nameof(NoticeDraft), draft.Id, AuditLogOperation.Updated,
            "Tenant notice approval updated an existing delivery record"), auditNow);
        return new AtomicNoticeDeliveryResult(rendered.Id, draft.Id, destinations.Count);
    }

    private async Task<AtomicNoticeDeliveryResult> CompleteExistingDeliveryAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        IReadOnlyList<DeliveryProjection> destinations,
        DeliveryFoundation foundation,
        string contentHash,
        string messagePreview,
        string templateProvenance,
        DateTime now,
        DateTime auditNow,
        CancellationToken ct)
    {
        if (rendered.WorkspaceNoticeTemplateVersionId != foundation.TemplateId
            || rendered.LeaseManagementId != draft.LeaseManagementId
            || rendered.Subject != draft.Subject
            || rendered.Body != draft.Body
            || rendered.ContentSha256 != contentHash
            || rendered.TemplateProvenance != templateProvenance
            || NormalizeJurisdiction(rendered.JurisdictionCode) != NormalizeJurisdiction(foundation.TemplateJurisdictionCode))
        {
            throw new InvalidOperationException(
                "Existing rendered notice does not match this approval request.");
        }

        TenantNoticeWorkItem? completedWorkItem = null;
        if (command.WorkItemId is not null)
        {
            var workItem = await _db.Set<TenantNoticeWorkItem>()
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

        var recipientConversationId = await ResolveRecipientConversationIdAsync(
            command, attempt, draft, rendered, ct);
        var approvedChannels = string.Join(",", command.Channels);
        var approvedAtUtc = draft.Status == "Approved"
            ? draft.ApprovedAt ?? rendered.ApprovedAtUtc
            : rendered.ApprovedAtUtc ?? now;
        if (approvedAtUtc is null)
        {
            throw new InvalidOperationException("Existing rendered notice is not approved.");
        }

        if (draft.Status == "Approved")
        {
            if (draft.RenderedNoticeId != rendered.Id
                || draft.ApprovedChannels != approvedChannels
                || rendered.ApprovedAtUtc != approvedAtUtc.Value)
            {
                throw new InvalidOperationException(
                    "Existing approved notice does not match this approval request.");
            }
        }
        else
        {
            await AtomicNoticeDraftPersistence.CompleteApprovalAsync(_db,
                attempt,
                command.PortfolioId,
                draft.Id,
                rendered.Id,
                recipientConversationId,
                approvedChannels,
                approvedAtUtc.Value,
                now,
                ct);
            await attempt.FlushBusinessAsync(ct);
        }

        var reconciledNotifications = await ReconcileApprovalNotificationIntentAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            messagePreview,
            approvedAtUtc.Value,
            ct);
        var validation = await RequireApprovalCompletionAsync(
            command,
            attempt,
            draft,
            rendered,
            destinations,
            recipientConversationId,
            approvedChannels,
            messagePreview,
            approvedAtUtc.Value,
            ct);
        attempt.StageSemanticEvent(Audit(
            command, nameof(NoticeDraft), draft.Id, AuditLogOperation.Updated,
            "Tenant notice approval recovered an existing delivery record"), auditNow);
        if (reconciledNotifications > 0)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(Notification), 0, AuditLogOperation.Updated,
                $"Updated {reconciledNotifications} tenant payment notification(s)"), auditNow);
        }
        if (completedWorkItem is not null)
        {
            attempt.StageSemanticEvent(Audit(
                command, nameof(TenantNoticeWorkItem), checked((int)completedWorkItem.Id),
                AuditLogOperation.Updated, "Tenant notice automation work completed"), auditNow);
        }
        return new AtomicNoticeDeliveryResult(rendered.Id, draft.Id, destinations.Count);
    }

    private Task<int> ReconcileApprovalNotificationIntentAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        IReadOnlyList<DeliveryProjection> destinations,
        string messagePreview,
        DateTime approvedAtUtc,
        CancellationToken ct) =>
        AtomicNoticeDraftPersistence.ReconcileApprovalNotificationIntentAsync(_db,
            attempt,
            command.PortfolioId,
            draft.Id,
            rendered.Id,
            rendered.Subject,
            messagePreview,
            approvedAtUtc,
            destinations.Select(destination => destination.LeaseManagementPartyId).ToArray(),
            destinations.Select(destination => destination.Channel).ToArray(),
            destinations.Select(destination => destination.RecipientUserId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessContextId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessRevision.GetValueOrDefault()).ToArray(),
            ct);

    private Task<int?> ResolveRecipientConversationIdAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        CancellationToken ct)
    {
        return (
            from evidence in _db.Set<NoticeDeliveryEvidence>().AsNoTracking()
            join message in _db.Set<ConversationMessage>().AsNoTracking()
                on evidence.ConversationMessageId equals message.Id
            where evidence.PortfolioId == command.PortfolioId
                && evidence.RenderedNoticeId == rendered.Id
                && evidence.RecipientLeaseManagementPartyId == draft.RecipientLeaseManagementPartyId
                && evidence.Channel == NoticeDeliveryChannel.TenantPortal
            select (int?)message.ConversationId)
            .SingleOrDefaultAsync(ct);
    }

    private async Task<AtomicNoticeApprovalCompletionValidation> RequireApprovalCompletionAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        IReadOnlyList<DeliveryProjection> destinations,
        int? expectedConversationId,
        string approvedChannels,
        string messagePreview,
        DateTime approvedAtUtc,
        CancellationToken ct)
    {
        var validation = await AtomicNoticeDraftPersistence.ValidateApprovalCompletionAsync(_db,
            attempt,
            command.PortfolioId,
            draft.Id,
            rendered.Id,
            rendered.Subject,
            messagePreview,
            approvedAtUtc,
            approvedChannels,
            expectedConversationId,
            draft.RecipientLeaseManagementPartyId,
            destinations.Select(destination => destination.LeaseManagementPartyId).ToArray(),
            destinations.Select(destination => destination.Channel).ToArray(),
            destinations.Select(destination => destination.Destination).ToArray(),
            destinations.Select(destination => DeliveryKey(rendered.Id, destination)).ToArray(),
            destinations.Select(destination => destination.RecipientUserId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessContextId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessRevision.GetValueOrDefault()).ToArray(),
            ct);
        if (!validation.IsMatch)
        {
            throw new InvalidOperationException(
                "Could not save complete tenant notice delivery details.");
        }

        return validation;
    }

    private async Task<AtomicNoticeApprovalCompletionValidation> RequireApprovalRecoveryCandidateAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext attempt,
        NoticeDraft draft,
        RenderedNotice rendered,
        IReadOnlyList<DeliveryProjection> destinations,
        int? expectedConversationId,
        string approvedChannels,
        string messagePreview,
        DateTime approvedAtUtc,
        CancellationToken ct)
    {
        var validation = await AtomicNoticeDraftPersistence.ValidateApprovalRecoveryCandidateAsync(_db,
            attempt,
            command.PortfolioId,
            draft.Id,
            rendered.Id,
            rendered.Subject,
            messagePreview,
            approvedAtUtc,
            approvedChannels,
            expectedConversationId,
            draft.RecipientLeaseManagementPartyId,
            destinations.Select(destination => destination.LeaseManagementPartyId).ToArray(),
            destinations.Select(destination => destination.Channel).ToArray(),
            destinations.Select(destination => destination.Destination).ToArray(),
            destinations.Select(destination => DeliveryKey(rendered.Id, destination)).ToArray(),
            destinations.Select(destination => destination.RecipientUserId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessContextId.GetValueOrDefault()).ToArray(),
            destinations.Select(destination => destination.NavigationAccessRevision.GetValueOrDefault()).ToArray(),
            ct);
        if (!validation.IsMatch)
        {
            throw new InvalidOperationException(
                "Approved tenant notice is not a recoverable delivery graph.");
        }

        return validation;
    }

    public async Task AuthorizeReplayAsync(
        AtomicNoticeDeliveryCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeCallerAsync(command, _db, now, requireActiveClaim: false, ct);
    }

    private async Task AuthorizeCallerAsync(
        AtomicNoticeDeliveryCommand command,
        RentalCommandDbContext db,
        DateTime now,
        bool requireActiveClaim,
        CancellationToken ct)
    {
        if (command.ActorUserId is not null)
        {
            if (!await db.Set<NoticeDraft>().AsNoTracking().AnyAsync(draft =>
                    draft.Id == command.NoticeDraftId
                    && draft.PortfolioId == command.PortfolioId
                    && draft.PropertyId != null
                    && AuthorizedProperties(command, db, now).Any(property =>
                        property.Id == draft.PropertyId.Value
                        && property.PortfolioId == draft.PortfolioId), ct))
            {
                throw new NoticeApprovalAuthorizationException(
                    "The current Team role cannot manage this tenant notice.");
            }
            return;
        }

        var workAuthorized = await db.Set<TenantNoticeWorkItem>().AsNoTracking().AnyAsync(row =>
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
            throw new NoticeApprovalAuthorizationException("Tenant notice automation claim is no longer valid.");
        }
    }

    internal static IQueryable<Property> AuthorizedProperties(
        AtomicNoticeDeliveryCommand command,
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.AuthorizedAssignmentsForScope(
            new WorkspaceReadScope(
                command.PortfolioId,
                command.ActorUserId!.Value,
                command.AuthSessionId!.Value,
                command.AccessContextId!.Value,
                command.ExpectedAccessRevision!.Value),
            [CapabilityKeys.TenantNoticesManage],
            CapabilityAuthorizationTargetKind.Property,
            now);
        return db.Set<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId
            && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                && assignment.SelectedProperties.Any(selected =>
                    selected.PortfolioId == property.PortfolioId
                    && selected.PropertyId == property.Id)));
    }

    private (string MessageType, string Payload) Payload(
        RenderedNotice rendered,
        NoticeDraft draft,
        DeliveryProjection destination,
        ConversationMessage? portalMessage,
        DateTime now) => destination.Channel switch
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
            navigationIntent = PushNavigationIntent(draft, destination, now),
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

    private NoticeRecipientRole RecipientRole(LeaseManagementPartyRole role) => role switch
    {
        LeaseManagementPartyRole.PrimaryTenant => NoticeRecipientRole.PrimaryTenant,
        LeaseManagementPartyRole.CoTenant => NoticeRecipientRole.CoTenant,
        LeaseManagementPartyRole.Guarantor => NoticeRecipientRole.Guarantor,
        LeaseManagementPartyRole.Occupant => NoticeRecipientRole.Occupant,
        _ => throw new InvalidOperationException($"Unsupported tenant notice recipient role {role}."),
    };

    private AtomicSemanticAudit Audit(
        AtomicNoticeDeliveryCommand command,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        string reason) =>
        new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private string? NormalizeJurisdiction(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private string ContentHash(string subject, string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\n" + body)))
            .ToLowerInvariant();

    private string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))
            .ToLowerInvariant()[..16];

    private string DeliveryKey(long renderedNoticeId, DeliveryProjection destination) =>
        $"notice:{renderedNoticeId}:party:{destination.LeaseManagementPartyId}:{destination.Channel}:{DestinationHash(destination.Destination)}";

    private string NoticeMessagePreview(string body) =>
        body.Length <= 280 ? body : body[..280];

    private NavigationDestination NoticeNavigationDestination(NoticeDraft draft) =>
        draft.TenantLedgerEntryId is null
            ? NavigationDestination.Message
            : NavigationDestination.TenantLedgerEntry;

    private string NoticeNavigationResourceKind(NoticeDraft draft) =>
        draft.TenantLedgerEntryId is null
            ? nameof(Conversation)
            : nameof(TenantLedgerEntry);

    private int NoticeNavigationResourceId(NoticeDraft draft, ConversationMessage portalMessage) =>
        draft.TenantLedgerEntryId is null
            ? portalMessage.ConversationId
            : checked((int)draft.TenantLedgerEntryId.Value);

    private string? NoticeNavigationParentResourceKind(NoticeDraft draft) =>
        draft.TenantLedgerEntryId is null ? null : nameof(TenantAccount);

    private int? NoticeNavigationParentResourceId(NoticeDraft draft) =>
        draft.TenantLedgerEntryId is null ? null : draft.TenantAccountId;

    private string NoticeRelatedEntityType(NoticeDraft draft) =>
        draft.TenantLedgerEntryId is null
            ? nameof(Conversation)
            : nameof(TenantLedgerEntry);

    private int NoticeRelatedEntityId(NoticeDraft draft, ConversationMessage portalMessage) =>
        draft.TenantLedgerEntryId is null
            ? portalMessage.ConversationId
            : checked((int)draft.TenantLedgerEntryId.Value);

    private object? PushNavigationIntent(
        NoticeDraft draft,
        DeliveryProjection destination,
        DateTime now)
    {
        if (destination.NavigationAccessContextId is null || destination.NavigationAccessRevision is null)
        {
            return null;
        }

        if (draft.TenantLedgerEntryId is not null)
        {
            return new
            {
                experience = NavigationExperience.Tenant.ToString(),
                destination = NavigationDestination.TenantLedgerEntry.ToString(),
                accessContextId = destination.NavigationAccessContextId.Value,
                accessRevision = destination.NavigationAccessRevision.Value,
                resource = new
                {
                    kind = nameof(TenantLedgerEntry),
                    id = checked((int)draft.TenantLedgerEntryId.Value),
                },
                parentResource = new
                {
                    kind = nameof(TenantAccount),
                    id = draft.TenantAccountId,
                },
                childResource = (object?)null,
                action = NavigationAction.Open.ToString(),
                expiresAtUtc = now.AddDays(7),
                fallbackDestination = NavigationDestination.Home.ToString(),
            };
        }

        return new
        {
            experience = NavigationExperience.Tenant.ToString(),
            destination = NavigationDestination.Notifications.ToString(),
            accessContextId = destination.NavigationAccessContextId.Value,
            accessRevision = destination.NavigationAccessRevision.Value,
            resource = (object?)null,
            parentResource = (object?)null,
            childResource = (object?)null,
            action = NavigationAction.Review.ToString(),
            expiresAtUtc = now.AddDays(7),
            fallbackDestination = NavigationDestination.Home.ToString(),
        };
    }

    private void Validate(AtomicNoticeDeliveryCommand command)
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
        int? NavigationAccessContextId,
        long? NavigationAccessRevision,
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
