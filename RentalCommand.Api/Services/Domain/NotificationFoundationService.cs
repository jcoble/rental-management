using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Api.Services.Domain;

public sealed class NotificationFoundationService : INotificationFoundationService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _clock;

    public NotificationFoundationService(RentalCommandDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<MyAlertsResponse> GetMyAlertsAsync(int portfolioId, int userId, CancellationToken ct) =>
        await MyAlertsQuery(portfolioId, userId).SingleAsync(ct);

    public async Task<MyAlertsResponse> UpdateMyAlertsAsync(
        int portfolioId, int userId, UpdateMyAlertsRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var belongsToWorkspace = await _db.WorkspaceAccessContexts.AsNoTracking()
            .AnyAsync(context => context.UserId == userId && context.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null, ct);
        if (!belongsToWorkspace) throw new KeyNotFoundException("User is not in the selected workspace.");

        var row = await _db.UserAlertPreferences
            .SingleOrDefaultAsync(x => x.PortfolioId == portfolioId && x.UserId == userId, ct);
        if (row is null)
        {
            row = new UserAlertPreference { PortfolioId = portfolioId, UserId = userId, CreatedAtUtc = now };
            _db.UserAlertPreferences.Add(row);
        }
        row.EnableInApp = request.EnableInApp;
        row.EnableMobilePush = request.EnableMobilePush;
        row.EnableEmail = request.EnableEmail;
        row.EnableSms = request.EnableSms;
        row.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return await MyAlertsQuery(portfolioId, userId).SingleAsync(ct);
    }

    public async Task ReplaceTeamRoutingRuleAsync(
        int portfolioId, UpsertTeamRoutingRuleRequest request, CancellationToken ct)
    {
        var requestedUserIds = request.Recipients.Select(recipient => recipient.UserId).Distinct().ToArray();
        if (requestedUserIds.Length != request.Recipients.Count)
            throw new InvalidOperationException("A named recipient may appear only once in a routing rule.");
        var now = _clock.GetUtcNow().UtcDateTime;
        var validUserCount = await (
            from user in _db.Users.AsNoTracking()
            join context in _db.WorkspaceAccessContexts.AsNoTracking() on user.Id equals context.UserId
            join membership in _db.WorkspaceMemberships.AsNoTracking() on context.Id equals membership.AccessContextId
            where requestedUserIds.Contains(user.Id)
                && context.PortfolioId == portfolioId && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.PortfolioId == portfolioId && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
            select user.Id).Distinct().CountAsync(ct);
        if (validUserCount != requestedUserIds.Length)
            throw new InvalidOperationException("Every named recipient must be an active user in this workspace.");
        if (request.PropertyId is int propertyId && !await _db.Properties.AsNoTracking()
            .AnyAsync(property => property.Id == propertyId && property.PortfolioId == portfolioId, ct))
            throw new InvalidOperationException("The routing property is outside this workspace.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var existing = await _db.TeamRoutingRules
            .Include(rule => rule.Recipients)
            .SingleOrDefaultAsync(rule => rule.PortfolioId == portfolioId && rule.Topic == request.Topic &&
                rule.PropertyId == request.PropertyId, ct);
        if (existing is null)
        {
            existing = new TeamRoutingRule
            {
                PortfolioId = portfolioId, Topic = request.Topic, PropertyId = request.PropertyId,
                CreatedAtUtc = now,
            };
            _db.TeamRoutingRules.Add(existing);
        }
        else
        {
            _db.TeamRoutingRuleRecipients.RemoveRange(existing.Recipients);
        }
        existing.UseWorkspaceAdministratorFallback = request.UseWorkspaceAdministratorFallback;
        existing.UpdatedAtUtc = now;
        existing.Recipients = request.Recipients.Select(recipient => new TeamRoutingRuleRecipient
        {
            PortfolioId = portfolioId, UserId = recipient.UserId, Reason = recipient.Reason.Trim(),
        }).ToList();
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<TeamRoutingRecipientPreview>> PreviewTeamRoutingAsync(
        int portfolioId, int ruleId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var explicitRecipients =
            from recipient in _db.TeamRoutingRuleRecipients.AsNoTracking()
            join rule in _db.TeamRoutingRules.AsNoTracking() on recipient.TeamRoutingRuleId equals rule.Id
            join user in _db.Users.AsNoTracking() on recipient.UserId equals user.Id
            join context in _db.WorkspaceAccessContexts.AsNoTracking()
                on new { UserId = user.Id, PortfolioId = portfolioId }
                equals new { context.UserId, context.PortfolioId }
            join membership in _db.WorkspaceMemberships.AsNoTracking()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            where rule.Id == ruleId && rule.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
            select new TeamRoutingRecipientPreview(user.Id, user.DisplayName, user.Email, rule.PropertyId,
                rule.PropertyId == null ? "All properties" : "Selected property", recipient.Reason, false);

        var administratorFallback =
            from rule in _db.TeamRoutingRules.AsNoTracking()
            from assignment in _db.MembershipRoleAssignments.AsNoTracking()
            join membership in _db.WorkspaceMemberships.AsNoTracking()
                on assignment.WorkspaceMembershipId equals membership.Id
            join context in _db.WorkspaceAccessContexts.AsNoTracking() on membership.AccessContextId equals context.Id
            join user in _db.Users.AsNoTracking() on context.UserId equals user.Id
            where rule.Id == ruleId && rule.PortfolioId == portfolioId && rule.UseWorkspaceAdministratorFallback
                && !_db.TeamRoutingRuleRecipients.Any(recipient => recipient.TeamRoutingRuleId == rule.Id)
                && assignment.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.RevokedAtUtc == null && assignment.SuspendedAtUtc == null
                && assignment.EffectiveFromUtc <= now && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Key == RoleProfileKeys.WorkspaceAdministrator
            select new TeamRoutingRecipientPreview(user.Id, user.DisplayName, user.Email, rule.PropertyId,
                "Workspace", "No named recipient is assigned; active Workspace Administrators receive this topic.", true);

        return await explicitRecipients.Concat(administratorFallback)
            .Distinct().OrderBy(row => row.DisplayName).ThenBy(row => row.UserId)
            .TagWith("TSK-668 Team routing named-recipient preview with visible administrator fallback")
            .ToListAsync(ct);
    }

    public async Task UpsertTenantNoticePolicyAsync(
        int portfolioId, int actorUserId, UpsertTenantNoticePolicyRequest request, CancellationToken ct)
    {
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal &&
            (!request.ConfirmJurisdictionReviewed || string.IsNullOrWhiteSpace(request.ReviewedJurisdictionCode)))
            throw new InvalidOperationException("Legal notices require explicit jurisdiction and template review before Auto is allowed.");
        if (request.IncludeOccupant && request.Classification is NoticeClassification.Legal)
            throw new InvalidOperationException("Occupants are not eligible for legal notices merely because they reside at the property.");

        var template = await LatestTemplateQuery(portfolioId)
            .SingleOrDefaultAsync(row => row.Id == request.WorkspaceNoticeTemplateVersionId, ct)
            ?? throw new KeyNotFoundException("Template version is not current for this workspace.");
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal &&
            template.JurisdictionReviewedAtUtc is null)
            throw new InvalidOperationException("The selected legal template version has not been reviewed.");

        var row = await _db.TenantNoticePolicies
            .SingleOrDefaultAsync(x => x.PortfolioId == portfolioId && x.AutomationKey == request.AutomationKey, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new TenantNoticePolicy { PortfolioId = portfolioId, AutomationKey = request.AutomationKey.Trim(), CreatedAtUtc = now };
            _db.TenantNoticePolicies.Add(row);
        }
        row.Mode = request.Mode; row.Classification = request.Classification;
        row.LeadDays = Math.Clamp(request.LeadDays, 0, 365); row.SendHourLocal = Math.Clamp(request.SendHourLocal, 0, 23);
        row.SendTenantPortal = request.SendTenantPortal; row.SendMobilePush = request.SendMobilePush;
        row.SendEmail = request.SendEmail; row.SendSms = request.SendSms;
        row.IncludePrimaryTenant = request.IncludePrimaryTenant; row.IncludeCoTenant = request.IncludeCoTenant;
        row.IncludeEligibleGuarantor = request.IncludeEligibleGuarantor; row.IncludeOccupant = request.IncludeOccupant;
        row.FailureBehavior = request.FailureBehavior; row.WorkspaceNoticeTemplateVersionId = template.Id;
        row.ReviewedJurisdictionCode = request.ConfirmJurisdictionReviewed ? request.ReviewedJurisdictionCode?.Trim() : null;
        row.JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null;
        row.JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? actorUserId : null;
        row.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct) =>
        await TemplateResponses(portfolioId).OrderBy(row => row.SystemKey).ToListAsync(ct);

    public async Task SeedSuppliedTemplatesAsync(int portfolioId, int actorUserId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            SuppliedNoticeTemplateBaseline.BuildWorkspaceV1CopyCommand(
                portfolioId,
                actorUserId,
                now),
            ct);
    }

    public Task<WorkspaceNoticeTemplateResponse> CreateTemplateVersionAsync(int portfolioId, int actorUserId,
        string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, CancellationToken ct) =>
        CreateTemplateVersionCoreAsync(portfolioId, actorUserId, systemKey, request, false, ct);

    public async Task<WorkspaceNoticeTemplateResponse> RestoreDefaultAsync(
        int portfolioId, int actorUserId, string systemKey, CancellationToken ct)
    {
        var currentSystem = await CurrentSystemTemplateQuery()
            .SingleAsync(row => row.SystemKey == systemKey, ct);
        return await CreateTemplateVersionCoreAsync(portfolioId, actorUserId, systemKey,
            new CreateWorkspaceNoticeTemplateVersionRequest(currentSystem.Subject, currentSystem.Body,
                currentSystem.JurisdictionCode, false), true, ct);
    }

    public async Task<long> ApproveAndQueueAsync(int portfolioId, int actorUserId, int draftId,
        ApproveAndQueueNoticeRequest request, CancellationToken ct)
    {
        if (request.Channels.Count == 0) throw new InvalidOperationException("At least one delivery channel is required.");
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var draft = await _db.NoticeDrafts.SingleOrDefaultAsync(row => row.Id == draftId && row.PortfolioId == portfolioId && row.Status == "Draft", ct)
            ?? throw new KeyNotFoundException("Draft does not exist or is no longer editable.");
        var policy = draft.TenantNoticePolicyId is null ? null :
            await _db.TenantNoticePolicies.SingleAsync(row => row.Id == draft.TenantNoticePolicyId && row.PortfolioId == portfolioId, ct);
        if (policy?.Mode == TenantNoticeMode.Off) throw new InvalidOperationException("A disabled policy cannot generate or deliver a notice.");
        if (policy is null) throw new InvalidOperationException("Draft has no tenant notice policy.");
        var templateId = draft.WorkspaceNoticeTemplateVersionId ?? policy?.WorkspaceNoticeTemplateVersionId
            ?? throw new InvalidOperationException("Draft has no immutable template version.");
        var template = await _db.WorkspaceNoticeTemplateVersions.Include(row => row.BasedOnSystemTemplateVersion)
            .SingleAsync(row => row.Id == templateId && row.PortfolioId == portfolioId, ct);
        if (policy is { Mode: TenantNoticeMode.Auto, Classification: NoticeClassification.Legal } &&
            (!policy.CanAutoSend || template.JurisdictionReviewedAtUtc is null))
            throw new InvalidOperationException("Legal Auto delivery requires reviewed jurisdiction and template facts.");

        var rendered = new RenderedNotice
        {
            PortfolioId = portfolioId, NoticeDraftId = draft.Id, WorkspaceNoticeTemplateVersionId = template.Id,
            LeaseManagementId = draft.LeaseManagementId, Subject = draft.Subject, Body = draft.Body,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Subject + "\n" + draft.Body))).ToLowerInvariant(),
            TemplateProvenance = $"{template.SystemKey}:workspace-v{template.Version}:system-v{template.BasedOnSystemTemplateVersion!.Version}",
            JurisdictionCode = template.JurisdictionCode, RenderedAtUtc = now, ApprovedByUserId = actorUserId, ApprovedAtUtc = now,
        };
        _db.RenderedNotices.Add(rendered);
        await _db.SaveChangesAsync(ct);
        var today = DateOnly.FromDateTime(now);
        var eligibleParties =
            from party in _db.LeaseManagementParties.AsNoTracking()
            join tenant in _db.Tenants.AsNoTracking() on new { party.TenantId, party.PortfolioId } equals new { TenantId = tenant.Id, tenant.PortfolioId }
            where party.PortfolioId == portfolioId && party.LeaseManagementId == draft.LeaseManagementId
                && tenant.DeletedAt == null && party.EffectiveFrom <= today
                && (party.EffectiveThrough == null || party.EffectiveThrough >= today)
                && ((party.Role == LeaseManagementPartyRole.PrimaryTenant && policy.IncludePrimaryTenant)
                    || (party.Role == LeaseManagementPartyRole.CoTenant && policy.IncludeCoTenant)
                    || (party.Role == LeaseManagementPartyRole.Guarantor && policy.IncludeEligibleGuarantor &&
                        (policy.Classification != NoticeClassification.Legal || party.GuarantorLegalNoticeEligible))
                    || (party.Role == LeaseManagementPartyRole.Occupant && policy.IncludeOccupant &&
                        policy.Classification != NoticeClassification.Legal))
            select new
            {
                TenantId = tenant.Id,
                LeaseManagementPartyId = party.Id,
                party.Role,
                tenant.Email,
                tenant.Phone,
            };

        var portal = eligibleParties
            .Where(_ => request.Channels.Contains(NoticeDeliveryChannel.TenantPortal) && policy.SendTenantPortal)
            .Select(row => new DeliveryProjection(row.TenantId, row.Role, NoticeDeliveryChannel.TenantPortal, row.TenantId.ToString()));
        var email = eligibleParties
            .Where(row => request.Channels.Contains(NoticeDeliveryChannel.Email) && policy.SendEmail && row.Email != null && row.Email != "")
            .Select(row => new DeliveryProjection(row.TenantId, row.Role, NoticeDeliveryChannel.Email, row.Email!));
        var sms = eligibleParties
            .Where(row => request.Channels.Contains(NoticeDeliveryChannel.Sms) && policy.SendSms && row.Phone != null && row.Phone != "")
            .Select(row => new DeliveryProjection(row.TenantId, row.Role, NoticeDeliveryChannel.Sms, row.Phone!));
        var push =
            from party in eligibleParties
            join access in _db.EffectiveTenantAccess.AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = portfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            join device in _db.DeviceTokens.AsNoTracking() on access.UserId equals device.UserId
            where request.Channels.Contains(NoticeDeliveryChannel.MobilePush) && policy.SendMobilePush
                && device.PortfolioId == portfolioId
            select new DeliveryProjection(party.TenantId, party.Role, NoticeDeliveryChannel.MobilePush, device.Token);
        var destinations = await portal.Union(email).Union(sms).Union(push)
            .OrderBy(row => row.TenantId).ThenBy(row => row.Channel).ThenBy(row => row.Destination)
            .TagWith("TSK-668 exact effective tenant notice recipients and destinations")
            .ToListAsync(ct);
        if (destinations.Count == 0) throw new InvalidOperationException("No eligible recipient has a configured destination for the selected channels.");

        foreach (var destination in destinations)
        {
            var key = $"notice:{rendered.Id}:{destination.TenantId}:{destination.Channel}";
            var outbox = new OutboxMessage
            {
                PortfolioId = portfolioId, MessageType = "TenantNoticeDelivery",
                Payload = JsonSerializer.Serialize(new { renderedNoticeId = rendered.Id, destination.TenantId, destination.Channel, destination.Destination }),
                IdempotencyKey = key, CreatedAtUtc = now, NextAttemptAtUtc = now,
            };
            _db.NoticeDeliveryEvidence.Add(new NoticeDeliveryEvidence
            {
                PortfolioId = portfolioId, RenderedNoticeId = rendered.Id, RecipientTenantId = destination.TenantId,
                RecipientRole = destination.RecipientRole, Channel = destination.Channel, Destination = destination.Destination,
                OutboxMessage = outbox, IdempotencyKey = key, CreatedAtUtc = now,
            });
        }
        draft.Status = "Approved"; draft.ApprovedAt = now; draft.RenderedNoticeId = rendered.Id; draft.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return rendered.Id;
    }

    private sealed record DeliveryProjection(int TenantId, LeaseManagementPartyRole PartyRole,
        NoticeDeliveryChannel Channel, string Destination)
    {
        public NoticeRecipientRole RecipientRole => Enum.Parse<NoticeRecipientRole>(PartyRole.ToString());
    }

    private IQueryable<MyAlertsResponse> MyAlertsQuery(int portfolioId, int userId) =>
        from user in _db.Users.AsNoTracking()
        join context in _db.WorkspaceAccessContexts.AsNoTracking()
            on new { UserId = user.Id, PortfolioId = portfolioId }
            equals new { context.UserId, context.PortfolioId }
        where user.Id == userId && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
        join saved in _db.UserAlertPreferences.AsNoTracking()
            on new { PortfolioId = portfolioId, UserId = userId } equals new { saved.PortfolioId, saved.UserId } into preferences
        from preference in preferences.DefaultIfEmpty()
        select new MyAlertsResponse(user.Id, user.DisplayName, user.Email, user.PhoneNumber,
            preference == null || preference.EnableInApp, preference == null || preference.EnableMobilePush,
            preference == null || preference.EnableEmail, preference != null && preference.EnableSms);

    private IQueryable<WorkspaceNoticeTemplateVersion> LatestTemplateQuery(int portfolioId) =>
        _db.WorkspaceNoticeTemplateVersions.Where(row => row.PortfolioId == portfolioId &&
            !_db.WorkspaceNoticeTemplateVersions.Any(newer => newer.PortfolioId == row.PortfolioId &&
                newer.SystemKey == row.SystemKey && newer.Version > row.Version));

    private IQueryable<SystemNoticeTemplateVersion> CurrentSystemTemplateQuery() =>
        _db.SystemNoticeTemplateVersions.Where(row => !_db.SystemNoticeTemplateVersions.Any(newer =>
            newer.SystemKey == row.SystemKey && newer.Version > row.Version));

    private IQueryable<WorkspaceNoticeTemplateResponse> TemplateResponses(int portfolioId) =>
        from workspace in LatestTemplateQuery(portfolioId)
        join system in _db.SystemNoticeTemplateVersions on workspace.BasedOnSystemTemplateVersionId equals system.Id
        let updateAvailable = _db.SystemNoticeTemplateVersions.Any(candidate => candidate.SystemKey == workspace.SystemKey && candidate.Version > system.Version)
        select new WorkspaceNoticeTemplateResponse(workspace.Id, workspace.SystemKey, workspace.Version,
            workspace.BasedOnSystemTemplateVersionId, workspace.IsCustomized, workspace.Subject, workspace.Body,
            system.Classification, workspace.JurisdictionCode, workspace.JurisdictionReviewedAtUtc, updateAvailable, workspace.CreatedAtUtc);

    private async Task<WorkspaceNoticeTemplateResponse> CreateTemplateVersionCoreAsync(int portfolioId, int actorUserId,
        string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, bool restore, CancellationToken ct)
    {
        var current = await LatestTemplateQuery(portfolioId).SingleAsync(row => row.SystemKey == systemKey, ct);
        var system = restore ? await CurrentSystemTemplateQuery().SingleAsync(row => row.SystemKey == systemKey, ct) :
            await _db.SystemNoticeTemplateVersions.SingleAsync(row => row.Id == current.BasedOnSystemTemplateVersionId, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var next = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = portfolioId, SystemKey = systemKey, Version = current.Version + 1,
            BasedOnSystemTemplateVersionId = system.Id, IsCustomized = !restore,
            Subject = request.Subject.Trim(), Body = request.Body.Trim(), JurisdictionCode = request.JurisdictionCode?.Trim(),
            JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null,
            JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? actorUserId : null,
            CreatedByUserId = actorUserId, CreatedAtUtc = now,
        };
        _db.WorkspaceNoticeTemplateVersions.Add(next);
        await _db.SaveChangesAsync(ct);
        return await TemplateResponses(portfolioId).SingleAsync(row => row.Id == next.Id, ct);
    }

}
