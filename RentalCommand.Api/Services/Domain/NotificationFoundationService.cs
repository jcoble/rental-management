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

    public async Task<MorningBriefingSettingsResponse> GetMorningBriefingSettingsAsync(
        int portfolioId, CancellationToken ct) =>
        await MorningBriefingSettingsQuery(portfolioId).SingleAsync(ct);

    public async Task<MorningBriefingSettingsResponse> UpdateMorningBriefingSettingsAsync(
        int portfolioId, UpdateMorningBriefingSettingsRequest request, CancellationToken ct)
    {
        if (request.SendHourLocal is < 0 or > 23)
            throw new InvalidOperationException("Morning Briefing send hour must be between 0 and 23.");

        var row = await _db.AutomationSettings
            .SingleAsync(settings => settings.PortfolioId == portfolioId, ct);
        row.EnableMorningBriefing = request.Enabled;
        row.MorningBriefingSendHourLocal = request.SendHourLocal;
        row.MorningBriefingIncludeEmpty = request.IncludeEmpty;
        row.UpdatedAtUtc = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return await MorningBriefingSettingsQuery(portfolioId).SingleAsync(ct);
    }

    public async Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRoutingRulesAsync(
        int portfolioId, CancellationToken ct) =>
        await TeamRoutingRuleResponses(portfolioId)
            .OrderBy(row => row.Topic).ThenBy(row => row.PropertyId)
            .TagWith("TSK-668 Team routing rules with DB-side named-recipient summaries")
            .ToListAsync(ct);

    public async Task<TeamRoutingRuleResponse> ReplaceTeamRoutingRuleAsync(
        int portfolioId, UpsertTeamRoutingRuleRequest request, CancellationToken ct)
    {
        var requestedUserIds = request.Recipients.Select(recipient => recipient.UserId).Distinct().ToArray();
        if (requestedUserIds.Length != request.Recipients.Count)
            throw new InvalidOperationException("A named recipient may appear only once in a routing rule.");
        if (request.Recipients.Any(recipient => string.IsNullOrWhiteSpace(recipient.Reason)))
            throw new InvalidOperationException("Every named recipient needs a clear routing reason.");
        if ((request.Topic is TeamRoutingTopic.AccountAndSecurity or TeamRoutingTopic.MorningBriefing)
            && request.PropertyId is not null)
            throw new InvalidOperationException(
                $"{request.Topic} routing is workspace-wide and cannot target one property.");

        var (capabilityKey, targetKind) = RoutingAuthorityFor(request.Topic);
        var now = _clock.GetUtcNow().UtcDateTime;
        var validUserCount = await (
            from user in _db.Users.AsNoTracking()
            join context in _db.WorkspaceAccessContexts.AsNoTracking() on user.Id equals context.UserId
            join membership in _db.WorkspaceMemberships.AsNoTracking() on context.Id equals membership.AccessContextId
            join assignment in _db.MembershipRoleAssignments.AsNoTracking()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where requestedUserIds.Contains(user.Id)
                && context.PortfolioId == portfolioId && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.PortfolioId == portfolioId && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= now
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    (request.Topic == TeamRoutingTopic.MorningBriefing
                        && profileCapability.CapabilityDefinition!.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property
                        && (profileCapability.CapabilityDefinition.Key == CapabilityKeys.RentalsRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.WorkRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.MoneyBalancesRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.LeasingShowingsManage
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.LeasingOnboardingManage))
                    || (request.Topic != TeamRoutingTopic.MorningBriefing
                        && profileCapability.CapabilityDefinition!.Key == capabilityKey
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == targetKind))
                && (targetKind == CapabilityAuthorizationTargetKind.Workspace
                    || (request.PropertyId == null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope => scope.PortfolioId == portfolioId))))
                    || (request.PropertyId != null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope => scope.PortfolioId == portfolioId
                                    && scope.PropertyId == request.PropertyId)))))
            select user.Id).Distinct().CountAsync(ct);
        if (validUserCount != requestedUserIds.Length)
            throw new InvalidOperationException(
                "Every named recipient must hold the topic capability and property scope on one active assignment.");
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
                PortfolioId = portfolioId,
                Topic = request.Topic,
                PropertyId = request.PropertyId,
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
            PortfolioId = portfolioId,
            UserId = recipient.UserId,
            Reason = recipient.Reason.Trim(),
        }).ToList();
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await TeamRoutingRuleResponses(portfolioId).SingleAsync(row => row.Id == existing.Id, ct);
    }

    public async Task<IReadOnlyList<TeamRoutingRuleRecipientResponse>> ListTeamRoutingRuleRecipientsAsync(
        int portfolioId, int ruleId, CancellationToken ct) =>
        await (
            from recipient in _db.TeamRoutingRuleRecipients.AsNoTracking()
            join rule in _db.TeamRoutingRules.AsNoTracking() on recipient.TeamRoutingRuleId equals rule.Id
            join user in _db.Users.AsNoTracking() on recipient.UserId equals user.Id
            where rule.Id == ruleId && rule.PortfolioId == portfolioId
            orderby user.DisplayName, user.Id
            select new TeamRoutingRuleRecipientResponse(user.Id, user.DisplayName, user.Email, recipient.Reason))
            .TagWith("TSK-668 Team routing editor named recipients")
            .ToListAsync(ct);

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
            join assignment in _db.MembershipRoleAssignments.AsNoTracking()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where rule.Id == ruleId && rule.PortfolioId == portfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= now
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    (rule.Topic == TeamRoutingTopic.RentAndMoney
                        && profileCapability.CapabilityDefinition!.Key == CapabilityKeys.MoneyBalancesRead
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property)
                    || (rule.Topic == TeamRoutingTopic.ApplicationsAndLeasing
                        && profileCapability.CapabilityDefinition!.Key == CapabilityKeys.LeasingApplicationsManage
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property)
                    || (rule.Topic == TeamRoutingTopic.WorkOrders
                        && profileCapability.CapabilityDefinition!.Key == CapabilityKeys.WorkRead
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property)
                    || (rule.Topic == TeamRoutingTopic.OwnerStatementsAndDecisions
                        && profileCapability.CapabilityDefinition!.Key == CapabilityKeys.MoneyOwnerReportsRead
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property)
                    || (rule.Topic == TeamRoutingTopic.AccountAndSecurity
                        && profileCapability.CapabilityDefinition!.Key == CapabilityKeys.SecurityManage
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace)
                    || (rule.Topic == TeamRoutingTopic.MorningBriefing
                        && profileCapability.CapabilityDefinition!.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property
                        && (profileCapability.CapabilityDefinition.Key == CapabilityKeys.RentalsRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.WorkRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.MoneyBalancesRead
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.LeasingShowingsManage
                            || profileCapability.CapabilityDefinition.Key == CapabilityKeys.LeasingOnboardingManage)))
                && (rule.Topic == TeamRoutingTopic.AccountAndSecurity
                    || (rule.PropertyId == null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope => scope.PortfolioId == portfolioId))))
                    || (rule.PropertyId != null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope => scope.PortfolioId == portfolioId
                                    && scope.PropertyId == rule.PropertyId)))))
            select new TeamRoutingRecipientPreview(user.Id, user.DisplayName, user.Email, rule.PropertyId,
                rule.Topic == TeamRoutingTopic.AccountAndSecurity ? "Workspace" :
                rule.PropertyId == null ? "All in-scope properties" : "Selected property", recipient.Reason, false);

        var administratorFallback =
            from rule in _db.TeamRoutingRules.AsNoTracking()
            from assignment in _db.MembershipRoleAssignments.AsNoTracking()
            join membership in _db.WorkspaceMemberships.AsNoTracking()
                on assignment.WorkspaceMembershipId equals membership.Id
            join context in _db.WorkspaceAccessContexts.AsNoTracking() on membership.AccessContextId equals context.Id
            join user in _db.Users.AsNoTracking() on context.UserId equals user.Id
            where rule.Id == ruleId && rule.PortfolioId == portfolioId && rule.UseWorkspaceAdministratorFallback
                && !explicitRecipients.Any()
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

    public async Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePoliciesAsync(
        int portfolioId, CancellationToken ct) =>
        await TenantNoticePolicyResponses(portfolioId)
            .OrderBy(row => row.AutomationKey)
            .TagWith("TSK-668 tenant notice policies with bound immutable template versions")
            .ToListAsync(ct);

    public async Task<TenantNoticePolicyResponse> UpsertTenantNoticePolicyAsync(
        int portfolioId, int actorUserId, UpsertTenantNoticePolicyRequest request, CancellationToken ct)
    {
        var reviewedJurisdiction = NormalizeJurisdiction(request.ReviewedJurisdictionCode);
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal &&
            (!request.ConfirmJurisdictionReviewed || reviewedJurisdiction is null))
            throw new InvalidOperationException("Legal notices require explicit jurisdiction and template review before Auto is allowed.");
        if (request.IncludeOccupant && request.Classification is NoticeClassification.Legal)
            throw new InvalidOperationException("Occupants are not eligible for legal notices merely because they reside at the property.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var template = await TemplateResponses(portfolioId)
            .SingleOrDefaultAsync(row => row.Id == request.WorkspaceNoticeTemplateVersionId, ct)
            ?? throw new KeyNotFoundException("Template version is not current for this workspace.");
        if (!string.Equals(template.SystemKey, request.AutomationKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected template must match the tenant notice automation.");
        if (request.Classification != template.Classification)
            throw new InvalidOperationException("The tenant notice classification must match its supplied template.");
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal &&
            (template.JurisdictionReviewedAtUtc is null
                || NormalizeJurisdiction(template.JurisdictionCode) != reviewedJurisdiction))
            throw new InvalidOperationException(
                "The selected legal template must be reviewed for the same jurisdiction as this automation.");

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
        row.ReviewedJurisdictionCode = request.ConfirmJurisdictionReviewed ? reviewedJurisdiction : null;
        row.JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null;
        row.JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? actorUserId : null;
        row.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await TenantNoticePolicyResponses(portfolioId).SingleAsync(policy => policy.Id == row.Id, ct);
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

    public Task<TenantNoticePolicyResponse> CreateTemplateVersionAsync(int portfolioId, int actorUserId,
        string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, CancellationToken ct) =>
        CreateTemplateVersionCoreAsync(portfolioId, actorUserId, systemKey, request, false, ct);

    public async Task<TenantNoticePolicyResponse> RestoreDefaultAsync(
        int portfolioId, int actorUserId, string systemKey, CancellationToken ct)
    {
        var currentSystem = await CurrentSystemTemplateQuery()
            .SingleAsync(row => row.SystemKey == systemKey, ct);
        return await CreateTemplateVersionCoreAsync(portfolioId, actorUserId, systemKey,
            new CreateWorkspaceNoticeTemplateVersionRequest(currentSystem.Subject, currentSystem.Body,
                currentSystem.JurisdictionCode, false), true, ct);
    }

    public async Task<long> ApproveAndQueueAsync(int portfolioId, int? actorUserId, int draftId,
        ApproveAndQueueNoticeRequest request, TenantNoticeWorkFence? workFence, CancellationToken ct)
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
        var policyJurisdiction = NormalizeJurisdiction(policy.ReviewedJurisdictionCode);
        var templateJurisdiction = NormalizeJurisdiction(template.JurisdictionCode);
        if (policy is { Mode: TenantNoticeMode.Auto, Classification: NoticeClassification.Legal } &&
            (!policy.CanAutoSend || template.JurisdictionReviewedAtUtc is null
                || policyJurisdiction is null || templateJurisdiction != policyJurisdiction))
            throw new InvalidOperationException("Legal Auto delivery requires reviewed jurisdiction and template facts.");

        var rendered = new RenderedNotice
        {
            PortfolioId = portfolioId,
            NoticeDraftId = draft.Id,
            WorkspaceNoticeTemplateVersionId = template.Id,
            LeaseManagementId = draft.LeaseManagementId,
            Subject = draft.Subject,
            Body = draft.Body,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.Subject + "\n" + draft.Body))).ToLowerInvariant(),
            TemplateProvenance = $"{template.SystemKey}:workspace-v{template.Version}:system-v{template.BasedOnSystemTemplateVersion!.Version}",
            JurisdictionCode = template.JurisdictionCode,
            RenderedAtUtc = now,
            ApprovedByUserId = actorUserId,
            ApprovedAtUtc = now,
        };
        _db.RenderedNotices.Add(rendered);
        await _db.SaveChangesAsync(ct);
        var today = DateOnly.FromDateTime(now);
        var eligibleParties =
            from party in _db.LeaseManagementParties.AsNoTracking()
            join tenant in _db.Tenants.AsNoTracking() on new { party.TenantId, party.PortfolioId } equals new { TenantId = tenant.Id, tenant.PortfolioId }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { LeaseManagementId = party.LeaseManagementId, party.PortfolioId }
                equals new { LeaseManagementId = management.Id, management.PortfolioId }
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
                management.PropertyId,
                management.UnitId,
            };

        var portal = eligibleParties
            .Where(_ => request.Channels.Contains(NoticeDeliveryChannel.TenantPortal) && policy.SendTenantPortal)
            .Select(row => new DeliveryProjection(row.LeaseManagementPartyId, row.TenantId, row.Role,
                NoticeDeliveryChannel.TenantPortal, row.TenantId.ToString(), row.PropertyId, row.UnitId));
        var email = eligibleParties
            .Where(row => request.Channels.Contains(NoticeDeliveryChannel.Email) && policy.SendEmail && row.Email != null && row.Email != "")
            .Select(row => new DeliveryProjection(row.LeaseManagementPartyId, row.TenantId, row.Role,
                NoticeDeliveryChannel.Email, row.Email!, row.PropertyId, row.UnitId));
        var sms = eligibleParties
            .Where(row => request.Channels.Contains(NoticeDeliveryChannel.Sms) && policy.SendSms && row.Phone != null && row.Phone != "")
            .Select(row => new DeliveryProjection(row.LeaseManagementPartyId, row.TenantId, row.Role,
                NoticeDeliveryChannel.Sms, row.Phone!, row.PropertyId, row.UnitId));
        var push =
            from party in eligibleParties
            join access in _db.EffectiveTenantAccess.AsNoTracking()
                on new { party.LeaseManagementPartyId, PortfolioId = portfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            join device in _db.DeviceTokens.AsNoTracking() on access.UserId equals device.UserId
            where request.Channels.Contains(NoticeDeliveryChannel.MobilePush) && policy.SendMobilePush
                && device.PortfolioId == portfolioId
            select new DeliveryProjection(party.LeaseManagementPartyId, party.TenantId, party.Role,
                NoticeDeliveryChannel.MobilePush, device.Token, party.PropertyId, party.UnitId);
        var destinations = await portal.Union(email).Union(sms).Union(push)
            .OrderBy(row => row.TenantId).ThenBy(row => row.Channel).ThenBy(row => row.Destination)
            .TagWith("TSK-668 exact effective tenant notice recipients and destinations")
            .ToListAsync(ct);
        if (destinations.Count == 0) throw new InvalidOperationException("No eligible recipient has a configured destination for the selected channels.");

        foreach (var destination in destinations)
        {
            var destinationHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(destination.Destination)))
                .ToLowerInvariant()[..16];
            var key = $"notice:{rendered.Id}:party:{destination.LeaseManagementPartyId}:{destination.Channel}:{destinationHash}";
            var (messageType, payload) = destination.Channel switch
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
                NoticeDeliveryChannel.TenantPortal => ("data-update", JsonSerializer.Serialize(new
                {
                    entityType = "TenantNotice",
                    entityId = draft.Id,
                    data = new
                    {
                        renderedNoticeId = rendered.Id,
                        tenantId = destination.TenantId,
                        leaseManagementId = rendered.LeaseManagementId,
                    },
                })),
                _ => throw new InvalidOperationException($"Unsupported tenant notice channel {destination.Channel}."),
            };

            if (destination.Channel == NoticeDeliveryChannel.TenantPortal)
            {
                _db.PortalMessages.Add(new PortalMessage
                {
                    PortfolioId = portfolioId,
                    RecipientTenantId = destination.TenantId,
                    FromLandlord = true,
                    Channels = "Portal",
                    PropertyId = destination.PropertyId,
                    UnitId = destination.UnitId,
                    Subject = rendered.Subject,
                    Body = rendered.Body,
                    Status = PortalMessageStatus.Open,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            var outbox = new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = messageType,
                Payload = payload,
                IdempotencyKey = key,
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            };
            _db.NoticeDeliveryEvidence.Add(new NoticeDeliveryEvidence
            {
                PortfolioId = portfolioId,
                RenderedNoticeId = rendered.Id,
                RecipientLeaseManagementPartyId = destination.LeaseManagementPartyId,
                RecipientRole = destination.RecipientRole,
                Channel = destination.Channel,
                Destination = destination.Destination,
                OutboxMessage = outbox,
                IdempotencyKey = key,
                CreatedAtUtc = now,
            });
        }
        draft.Status = "Approved";
        draft.ApprovedAt = now;
        draft.ApprovedChannels = string.Join(",", request.Channels.OrderBy(channel => channel));
        draft.RenderedNoticeId = rendered.Id;
        draft.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        if (workFence is not null)
        {
            var completed = await _db.TenantNoticeWorkItems
                .Where(row => row.Id == workFence.WorkItemId
                    && row.PortfolioId == portfolioId
                    && row.Status == TenantNoticeWorkStatus.Claimed
                    && row.ClaimToken == workFence.ClaimToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, TenantNoticeWorkStatus.Completed)
                    .SetProperty(row => row.ClaimOwner, (string?)null)
                    .SetProperty(row => row.ClaimToken, (Guid?)null)
                    .SetProperty(row => row.ClaimExpiresAtUtc, (DateTime?)null), ct);
            if (completed != 1)
            {
                throw new DbUpdateConcurrencyException(
                    $"Tenant notice work item {workFence.WorkItemId} is no longer owned by this claim.");
            }
        }
        await transaction.CommitAsync(ct);
        return rendered.Id;
    }

    public async Task<IReadOnlyList<NoticeDeliveryStatusResponse>> ListDeliveryStatusesAsync(
        int portfolioId,
        int take,
        CancellationToken ct)
    {
        var boundedTake = Math.Clamp(take, 1, 100);
        return await (
                from evidence in _db.NoticeDeliveryEvidence.AsNoTracking()
                join rendered in _db.RenderedNotices.AsNoTracking()
                    on new { RenderedNoticeId = evidence.RenderedNoticeId, evidence.PortfolioId }
                    equals new { RenderedNoticeId = rendered.Id, rendered.PortfolioId }
                join outbox in _db.OutboxMessages.AsNoTracking() on evidence.OutboxMessageId equals outbox.Id
                where evidence.PortfolioId == portfolioId && outbox.PortfolioId == portfolioId
                orderby evidence.CreatedAtUtc descending, evidence.Id descending
                select new NoticeDeliveryStatusResponse(
                    evidence.Id,
                    rendered.Id,
                    rendered.NoticeDraftId,
                    rendered.Subject,
                    rendered.LeaseManagementId,
                    evidence.RecipientRole,
                    evidence.Channel,
                    evidence.Destination,
                    outbox.DeliveredAtUtc != null ? "Delivered" :
                    outbox.DeadLetteredAtUtc != null ? "Failed" :
                    outbox.AcceptedAtUtc != null ? "Accepted" :
                    outbox.AttemptCount > 0 ? "Retrying" : "Queued",
                    outbox.AttemptCount,
                    evidence.CreatedAtUtc,
                    outbox.LastAttemptAtUtc,
                    outbox.DeadLetteredAtUtc == null && outbox.AcceptedAtUtc == null
                        ? outbox.NextAttemptAtUtc
                        : null,
                    outbox.AcceptedAtUtc,
                    outbox.DeliveredAtUtc,
                    outbox.DeadLetteredAtUtc,
                    outbox.Provider,
                    outbox.ProviderMessageId,
                    outbox.LastError))
            .Take(boundedTake)
            .TagWith("TSK-668 recent durable tenant notice delivery status")
            .ToListAsync(ct);
    }

    private sealed record DeliveryProjection(int LeaseManagementPartyId, int TenantId, LeaseManagementPartyRole PartyRole,
        NoticeDeliveryChannel Channel, string Destination, int PropertyId, int UnitId)
    {
        public NoticeRecipientRole RecipientRole => Enum.Parse<NoticeRecipientRole>(PartyRole.ToString());
    }

    private static (string CapabilityKey, CapabilityAuthorizationTargetKind TargetKind) RoutingAuthorityFor(
        TeamRoutingTopic topic) => topic switch
    {
        TeamRoutingTopic.RentAndMoney =>
            (CapabilityKeys.MoneyBalancesRead, CapabilityAuthorizationTargetKind.Property),
        TeamRoutingTopic.ApplicationsAndLeasing =>
            (CapabilityKeys.LeasingApplicationsManage, CapabilityAuthorizationTargetKind.Property),
        TeamRoutingTopic.WorkOrders =>
            (CapabilityKeys.WorkRead, CapabilityAuthorizationTargetKind.Property),
        TeamRoutingTopic.OwnerStatementsAndDecisions =>
            (CapabilityKeys.MoneyOwnerReportsRead, CapabilityAuthorizationTargetKind.Property),
        TeamRoutingTopic.AccountAndSecurity =>
            (CapabilityKeys.SecurityManage, CapabilityAuthorizationTargetKind.Workspace),
        // Morning Briefing spans several operational domains. The tuple keeps its resource scope
        // property-based; recipient eligibility is evaluated as an OR across the permitted
        // operational capabilities in both replacement validation and preview.
        TeamRoutingTopic.MorningBriefing =>
            (CapabilityKeys.RentalsRead, CapabilityAuthorizationTargetKind.Property),
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown Team routing topic."),
    };

    private static string? NormalizeJurisdiction(string? jurisdictionCode) =>
        string.IsNullOrWhiteSpace(jurisdictionCode)
            ? null
            : jurisdictionCode.Trim().ToUpperInvariant();

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

    private IQueryable<MorningBriefingSettingsResponse> MorningBriefingSettingsQuery(int portfolioId) =>
        from settings in _db.AutomationSettings.AsNoTracking()
        join portfolio in _db.Portfolios.AsNoTracking() on settings.PortfolioId equals portfolio.Id
        where settings.PortfolioId == portfolioId && portfolio.DeletedAt == null
        select new MorningBriefingSettingsResponse(
            settings.EnableMorningBriefing,
            settings.MorningBriefingSendHourLocal,
            settings.MorningBriefingIncludeEmpty,
            portfolio.TimeZone == "" ? "America/New_York" : portfolio.TimeZone);

    private IQueryable<TeamRoutingRuleResponse> TeamRoutingRuleResponses(int portfolioId) =>
        from rule in _db.TeamRoutingRules.AsNoTracking()
        where rule.PortfolioId == portfolioId
        let recipientCount = rule.Recipients.Count()
        let recipientNames = string.Join(", ", rule.Recipients
            .OrderBy(recipient => recipient.User!.DisplayName)
            .ThenBy(recipient => recipient.UserId)
            .Select(recipient => recipient.User!.DisplayName))
        let recipientExplanations = string.Join(" ", rule.Recipients
            .OrderBy(recipient => recipient.User!.DisplayName)
            .ThenBy(recipient => recipient.UserId)
            .Select(recipient => recipient.User!.DisplayName + " receives this because " + recipient.Reason + "."))
        select new TeamRoutingRuleResponse(
            rule.Id,
            rule.Topic,
            rule.PropertyId,
            rule.PropertyId == null ? "All in-scope properties" : rule.Property!.Name,
            rule.UseWorkspaceAdministratorFallback,
            recipientCount,
            recipientCount == 0 ? "No named recipients" : recipientNames,
            recipientCount == 0 && rule.UseWorkspaceAdministratorFallback
                ? "No named recipient is assigned; active Workspace Administrators receive this topic."
                : recipientExplanations,
            rule.UpdatedAtUtc);

    private IQueryable<TenantNoticePolicyResponse> TenantNoticePolicyResponses(int portfolioId) =>
        from policy in _db.TenantNoticePolicies.AsNoTracking()
        join template in _db.WorkspaceNoticeTemplateVersions.AsNoTracking()
            on new { TemplateId = policy.WorkspaceNoticeTemplateVersionId, policy.PortfolioId }
            equals new { TemplateId = template.Id, template.PortfolioId }
        join basedOnSystem in _db.SystemNoticeTemplateVersions.AsNoTracking()
            on template.BasedOnSystemTemplateVersionId equals basedOnSystem.Id
        let templateUpdateAvailable = _db.SystemNoticeTemplateVersions.Any(candidate =>
            candidate.SystemKey == template.SystemKey && candidate.Version > basedOnSystem.Version)
        where policy.PortfolioId == portfolioId
        select new TenantNoticePolicyResponse(
            policy.Id,
            policy.AutomationKey,
            policy.Mode,
            policy.Classification,
            policy.LeadDays,
            policy.SendHourLocal,
            policy.SendTenantPortal,
            policy.SendMobilePush,
            policy.SendEmail,
            policy.SendSms,
            policy.IncludePrimaryTenant,
            policy.IncludeCoTenant,
            policy.IncludeEligibleGuarantor,
            policy.IncludeOccupant,
            policy.FailureBehavior,
            policy.WorkspaceNoticeTemplateVersionId,
            template.SystemKey,
            template.Version,
            template.Subject,
            template.Body,
            template.BasedOnSystemTemplateVersionId,
            basedOnSystem.Provenance,
            template.IsCustomized,
            templateUpdateAvailable,
            template.CreatedAtUtc,
            template.JurisdictionCode,
            template.JurisdictionReviewedAtUtc,
            policy.ReviewedJurisdictionCode,
            policy.JurisdictionReviewedAtUtc,
            policy.Classification != NoticeClassification.Legal ||
                (policy.ReviewedJurisdictionCode != null && policy.JurisdictionReviewedAtUtc != null
                    && template.JurisdictionCode != null && template.JurisdictionReviewedAtUtc != null
                    && policy.ReviewedJurisdictionCode.Trim().ToUpper() ==
                        template.JurisdictionCode.Trim().ToUpper()),
            policy.UpdatedAtUtc);

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

    private async Task<TenantNoticePolicyResponse> CreateTemplateVersionCoreAsync(int portfolioId, int actorUserId,
        string systemKey, CreateWorkspaceNoticeTemplateVersionRequest request, bool restore, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var current = await LatestTemplateQuery(portfolioId)
            .SingleAsync(row => row.SystemKey == systemKey, ct);
        var system = restore ? await CurrentSystemTemplateQuery().SingleAsync(row => row.SystemKey == systemKey, ct) :
            await _db.SystemNoticeTemplateVersions.SingleAsync(row => row.Id == current.BasedOnSystemTemplateVersionId, ct);
        var policy = await _db.TenantNoticePolicies.SingleOrDefaultAsync(row =>
            row.PortfolioId == portfolioId && row.AutomationKey == systemKey, ct)
            ?? throw new KeyNotFoundException("The tenant notice automation policy was not provisioned.");
        var reviewedJurisdiction = NormalizeJurisdiction(request.JurisdictionCode);
        if (system.Classification == NoticeClassification.Legal && policy.Mode == TenantNoticeMode.Auto &&
            (!request.ConfirmJurisdictionReviewed || reviewedJurisdiction is null))
            throw new InvalidOperationException(
                "An automatically sent legal template must be explicitly reviewed for a jurisdiction before it is bound.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var next = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = portfolioId,
            SystemKey = systemKey,
            Version = current.Version + 1,
            BasedOnSystemTemplateVersionId = system.Id,
            IsCustomized = !restore,
            Subject = request.Subject.Trim(),
            Body = request.Body.Trim(),
            JurisdictionCode = reviewedJurisdiction,
            JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null,
            JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? actorUserId : null,
            CreatedByUserId = actorUserId,
            CreatedAtUtc = now,
        };
        _db.WorkspaceNoticeTemplateVersions.Add(next);
        policy.TemplateVersion = next;
        policy.Classification = system.Classification;
        policy.ReviewedJurisdictionCode = request.ConfirmJurisdictionReviewed
            ? reviewedJurisdiction
            : null;
        policy.JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null;
        policy.JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? actorUserId : null;
        policy.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await TenantNoticePolicyResponses(portfolioId)
            .SingleAsync(row => row.Id == policy.Id, ct);
    }

}
