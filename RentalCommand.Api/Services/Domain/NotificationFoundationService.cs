using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
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
    private readonly IAtomicUnitOfWork _atomic;

    public NotificationFoundationService(
        RentalCommandDbContext db,
        TimeProvider clock,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _clock = clock;
        _atomic = atomic;
    }

    public async Task<MyAlertsResponse> GetMyAlertsAsync(int portfolioId, int userId, CancellationToken ct) =>
        await MyAlertsQuery(portfolioId, userId).SingleAsync(ct);

    public async Task<MyAlertsResponse> UpdateMyAlertsAsync(
        WorkspaceReadScope scope,
        UpdateMyAlertsRequest request,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.MyAlerts, 0, string.Empty, operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<MyAlertsResponse>(outcome.Value);
    }

    public async Task<MorningBriefingSettingsResponse> GetMorningBriefingSettingsAsync(
        int portfolioId, CancellationToken ct) =>
        await MorningBriefingSettingsQuery(portfolioId).SingleAsync(ct);

    public async Task<MorningBriefingSettingsResponse> UpdateMorningBriefingSettingsAsync(
        WorkspaceReadScope scope,
        UpdateMorningBriefingSettingsRequest request,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.MorningBriefingSettings, 0, string.Empty,
            operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<MorningBriefingSettingsResponse>(outcome.Value);
    }

    public async Task<IReadOnlyList<TeamRoutingRuleResponse>> ListTeamRoutingRulesAsync(
        int portfolioId, CancellationToken ct) =>
        await TeamRoutingRuleResponses(portfolioId)
            .TagWith("TSK-668 Team routing rules with DB-side named-recipient summaries")
            .ToListAsync(ct);

    public async Task<TeamRoutingRuleResponse> ReplaceTeamRoutingRuleAsync(
        WorkspaceReadScope scope,
        UpsertTeamRoutingRuleRequest request,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.TeamRouting, 0, request.Topic.ToString(), operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<TeamRoutingRuleResponse>(outcome.Value);
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
                && context.AccessRevision > 0
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
            select new
            {
                UserId = user.Id,
                user.DisplayName,
                user.Email,
                user.PhoneNumber,
                EnableInApp = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableInApp),
                EnableMobilePush = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableMobilePush),
                EnableEmail = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableEmail),
                EnableSms = _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableSms),
                rule.PropertyId,
                Scope = rule.Topic == TeamRoutingTopic.AccountAndSecurity ? "Workspace" :
                    rule.PropertyId == null ? "All in-scope properties" : "Selected property",
                recipient.Reason,
                IsFallback = false
            };

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
                && context.AccessRevision > 0
                && membership.Status == WorkspaceMembershipStatus.Active && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.RevokedAtUtc == null && assignment.SuspendedAtUtc == null
                && assignment.EffectiveFromUtc <= now && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Key == RoleProfileKeys.WorkspaceAdministrator
            select new
            {
                UserId = user.Id,
                user.DisplayName,
                user.Email,
                user.PhoneNumber,
                EnableInApp = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableInApp),
                EnableMobilePush = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableMobilePush),
                EnableEmail = !_db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id)
                    || _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableEmail),
                EnableSms = _db.UserAlertPreferences.Any(preference => preference.PortfolioId == portfolioId && preference.UserId == user.Id && preference.EnableSms),
                rule.PropertyId,
                Scope = "Workspace",
                Reason = "No named recipient is assigned; active Workspace Administrators receive this topic.",
                IsFallback = true
            };

        return await explicitRecipients.Concat(administratorFallback)
            .Distinct().OrderBy(row => row.DisplayName).ThenBy(row => row.UserId)
            .Select(row => new TeamRoutingRecipientPreview(row.UserId, row.DisplayName, row.Email, row.PhoneNumber,
                row.EnableInApp, row.EnableMobilePush, row.EnableEmail, row.EnableSms, row.PropertyId,
                row.Scope, row.Reason, row.IsFallback))
            .TagWith("TSK-668 Team routing named-recipient preview with visible administrator fallback")
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TenantNoticePolicyResponse>> ListTenantNoticePoliciesAsync(
        int portfolioId, CancellationToken ct) =>
        await TenantNoticePolicyResponses(portfolioId)
            .TagWith("TSK-668 tenant notice policies with bound immutable template versions")
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TenantNoticeRecipientPreviewResponse>> PreviewTenantNoticeRecipientsAsync(
        int portfolioId,
        string automationKey,
        int leaseManagementId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(automationKey) || leaseManagementId <= 0)
            throw new ArgumentException("An automation key and lease-management relationship are required.");

        var rows = await (
                from policy in _db.TenantNoticePolicies.AsNoTracking()
                join party in _db.LeaseManagementParties.AsNoTracking()
                    on policy.PortfolioId equals party.PortfolioId
                join tenant in _db.Tenants.AsNoTracking()
                    on new { party.TenantId, party.PortfolioId }
                    equals new { TenantId = tenant.Id, tenant.PortfolioId }
                join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                    on new { party.LeaseManagementId, party.PortfolioId }
                    equals new { lifecycle.LeaseManagementId, lifecycle.PortfolioId }
                where policy.PortfolioId == portfolioId
                    && policy.AutomationKey == automationKey
                    && party.LeaseManagementId == leaseManagementId
                let effective = tenant.DeletedAt == null
                    && lifecycle.TenantAccountId != null
                    && !lifecycle.HasReconciliationException
                    && party.EffectiveFrom <= lifecycle.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
                let roleIncluded =
                    party.Role == LeaseManagementPartyRole.PrimaryTenant && policy.IncludePrimaryTenant
                    || party.Role == LeaseManagementPartyRole.CoTenant && policy.IncludeCoTenant
                    || party.Role == LeaseManagementPartyRole.Guarantor && policy.IncludeEligibleGuarantor
                        && policy.Classification == NoticeClassification.Legal
                        && party.GuarantorLegalNoticeEligible
                    || party.Role == LeaseManagementPartyRole.Occupant && policy.IncludeOccupant
                        && policy.Classification != NoticeClassification.Legal
                        && policy.AutomationKey != "rent-reminder"
                        && policy.AutomationKey != "late-rent-late-fee"
                let hasPortal = policy.SendTenantPortal && _db.EffectiveTenantAccess.Any(access =>
                    access.PortfolioId == portfolioId && access.LeaseManagementPartyId == party.Id)
                let hasPush = policy.SendMobilePush && (
                    from access in _db.EffectiveTenantAccess
                    join device in _db.DeviceTokens on access.UserId equals device.UserId
                    where access.PortfolioId == portfolioId
                        && access.LeaseManagementPartyId == party.Id
                        && device.PortfolioId == portfolioId
                    select device.Id).Any()
                let hasEmail = policy.SendEmail && tenant.Email != null && tenant.Email != ""
                let hasSms = policy.SendSms && tenant.Phone != null && tenant.Phone != ""
                select new TenantRecipientPreviewRow(
                    party.Id,
                    tenant.Id,
                    (tenant.FirstName + " " + tenant.LastName).Trim(),
                    party.Role == LeaseManagementPartyRole.PrimaryTenant ? NoticeRecipientRole.PrimaryTenant :
                    party.Role == LeaseManagementPartyRole.CoTenant ? NoticeRecipientRole.CoTenant :
                    party.Role == LeaseManagementPartyRole.Guarantor ? NoticeRecipientRole.Guarantor :
                    NoticeRecipientRole.Occupant,
                    effective && roleIncluded && (hasPortal || hasPush || hasEmail || hasSms),
                    hasPortal,
                    hasPush,
                    hasEmail,
                    hasSms,
                    tenant.Email,
                    tenant.Phone,
                    tenant.DeletedAt != null ? "The tenant record is inactive." :
                    lifecycle.TenantAccountId == null || lifecycle.HasReconciliationException
                        ? "The lease relationship must be reconciled before a notice can be delivered." :
                    !effective ? "This person is not effective in the relationship on the current business date." :
                    party.Role == LeaseManagementPartyRole.PrimaryTenant && !policy.IncludePrimaryTenant
                        ? "Primary tenants are excluded by this automation policy." :
                    party.Role == LeaseManagementPartyRole.CoTenant && !policy.IncludeCoTenant
                        ? "Co-tenants are excluded by this automation policy." :
                    party.Role == LeaseManagementPartyRole.Guarantor && !policy.IncludeEligibleGuarantor
                        ? "Guarantors are excluded by this automation policy." :
                    party.Role == LeaseManagementPartyRole.Guarantor
                        && policy.Classification != NoticeClassification.Legal
                        ? "Guarantors receive only explicitly designated legally relevant notices." :
                    party.Role == LeaseManagementPartyRole.Guarantor && policy.Classification == NoticeClassification.Legal
                        && !party.GuarantorLegalNoticeEligible
                        ? "This guarantor is not designated to receive this legal notice." :
                    party.Role == LeaseManagementPartyRole.Occupant
                        && (policy.Classification == NoticeClassification.Legal
                            || policy.AutomationKey == "rent-reminder"
                            || policy.AutomationKey == "late-rent-late-fee")
                        ? "Occupants never receive financial or legal notices merely because they reside here." :
                    party.Role == LeaseManagementPartyRole.Occupant && !policy.IncludeOccupant
                        ? "Occupants are excluded by this automation policy." :
                    !(hasPortal || hasPush || hasEmail || hasSms)
                        ? "No enabled channel has a valid destination for this person." :
                    "Eligible under the saved relationship role, channel, and automation policy."))
            .OrderBy(row => row.Role)
            .ThenBy(row => row.DisplayName)
            .ThenBy(row => row.LeaseManagementPartyId)
            .TagWith("TSK-668 exact tenant notice recipient eligibility, destinations, and exclusions")
            .ToListAsync(ct);

        return rows.Select(row => new TenantNoticeRecipientPreviewResponse(
            row.LeaseManagementPartyId,
            row.TenantId,
            row.DisplayName,
            row.Role,
            row.Eligible,
            Channels(row),
            row.Email,
            row.Phone,
            row.Reason)).ToArray();
    }

    public async Task<TenantNoticePolicyResponse> UpsertTenantNoticePolicyAsync(
        WorkspaceReadScope scope,
        UpsertTenantNoticePolicyRequest request,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.TenantNoticePolicy, 0, request.AutomationKey,
            operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<TenantNoticePolicyResponse>(outcome.Value);
    }

    public async Task<IReadOnlyList<WorkspaceNoticeTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct) =>
        await TemplateResponses(portfolioId).ToListAsync(ct);

    public IReadOnlyList<NoticeMergeFieldHelpResponse> ListMergeFields(string systemKey) =>
        NoticeMergeFields.HelpForType(systemKey);

    public async Task SeedSuppliedTemplatesAsync(
        WorkspaceReadScope scope,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.SeedTemplates, 0, "supplied-v1", operationKey, new { });
        await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
    }

    public async Task<TenantNoticePolicyResponse> CreateTemplateVersionAsync(
        WorkspaceReadScope scope,
        string systemKey,
        CreateWorkspaceNoticeTemplateVersionRequest request,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.TemplateVersion, 0, systemKey, operationKey, request);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<TenantNoticePolicyResponse>(outcome.Value);
    }

    public async Task<TenantNoticePolicyResponse> RestoreDefaultAsync(
        WorkspaceReadScope scope,
        string systemKey,
        string operationKey,
        CancellationToken ct)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.RestoreTemplate, 0, systemKey, operationKey, new { });
        var outcome = await _atomic.ExecuteAsync(
            AtomicNotificationMutation.Identity(command), command, AtomicNotificationMutation.Codec, ct);
        return ReadSnapshot<TenantNoticePolicyResponse>(outcome.Value);
    }

    public async Task<long> ApproveAndQueueAsync(
        NoticeApprovalExecutionContext context,
        int draftId,
        ApproveAndQueueNoticeRequest request,
        TenantNoticeWorkFence? workFence,
        string operationKey,
        CancellationToken ct)
    {
        if (request.Channels.Count == 0)
            throw new InvalidOperationException("At least one delivery channel is required.");
        var command = AtomicNoticeDelivery.Command(
            context, draftId, request.Channels, workFence, operationKey);
        var outcome = await _atomic.ExecuteAsync(
            AtomicNoticeDelivery.Identity(command), command, AtomicNoticeDelivery.Codec, ct);
        return outcome.Value.RenderedNoticeId;
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
                    evidence.Channel == NoticeDeliveryChannel.MobilePush ? "Registered mobile device" :
                    evidence.Channel == NoticeDeliveryChannel.TenantPortal ? "Tenant portal" :
                    evidence.Destination,
                    outbox.DeliveredAtUtc != null ? NoticeDeliveryState.Sent :
                    outbox.DeadLetteredAtUtc != null ? NoticeDeliveryState.PermanentlyFailed :
                    outbox.AcceptedAtUtc != null ? NoticeDeliveryState.Accepted :
                    outbox.AttemptCount > 0 ? NoticeDeliveryState.Retrying : NoticeDeliveryState.Queued,
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

    private IQueryable<MyAlertsResponse> MyAlertsQuery(int portfolioId, int userId) =>
        from context in _db.WorkspaceAccessContexts.AsNoTracking()
        join user in _db.Users.AsNoTracking() on context.UserId equals user.Id
        where context.PortfolioId == portfolioId && context.UserId == userId
            && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
        join saved in _db.UserAlertPreferences.AsNoTracking()
                .Select(preference => new
                {
                    preference.PortfolioId,
                    preference.UserId,
                    EnableInApp = (bool?)preference.EnableInApp,
                    EnableMobilePush = (bool?)preference.EnableMobilePush,
                    EnableEmail = (bool?)preference.EnableEmail,
                    EnableSms = (bool?)preference.EnableSms,
                })
            on new { context.PortfolioId, context.UserId }
            equals new { saved.PortfolioId, saved.UserId } into preferences
        from preference in preferences.DefaultIfEmpty()
        select new MyAlertsResponse(user.Id, user.DisplayName, user.Email, user.PhoneNumber,
            preference.EnableInApp ?? true,
            preference.EnableMobilePush ?? true,
            preference.EnableEmail ?? true,
            preference.EnableSms ?? false);

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
        orderby rule.Topic, rule.PropertyId
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
        where policy.PortfolioId == portfolioId
        orderby policy.AutomationKey
        let templateUpdateAvailable = _db.SystemNoticeTemplateVersions.Any(candidate =>
            candidate.SystemKey == template.SystemKey && candidate.Version > basedOnSystem.Version)
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
        orderby workspace.SystemKey
        let updateAvailable = _db.SystemNoticeTemplateVersions.Any(candidate => candidate.SystemKey == workspace.SystemKey && candidate.Version > system.Version)
        select new WorkspaceNoticeTemplateResponse(workspace.Id, workspace.SystemKey, workspace.Version,
            workspace.BasedOnSystemTemplateVersionId, workspace.IsCustomized, workspace.Subject, workspace.Body,
            system.Classification, workspace.JurisdictionCode, workspace.JurisdictionReviewedAtUtc, updateAvailable, workspace.CreatedAtUtc);

    private static TResponse ReadSnapshot<TResponse>(AtomicNotificationMutationResult result)
        where TResponse : class =>
        result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
                ?? throw new InvalidOperationException("Atomic notification result snapshot is invalid.")
            : throw new InvalidOperationException("Atomic notification result did not contain a response snapshot.");

    private static IReadOnlyList<NoticeDeliveryChannel> Channels(TenantRecipientPreviewRow row)
    {
        var channels = new List<NoticeDeliveryChannel>(4);
        if (row.HasPortal) channels.Add(NoticeDeliveryChannel.TenantPortal);
        if (row.HasPush) channels.Add(NoticeDeliveryChannel.MobilePush);
        if (row.HasEmail) channels.Add(NoticeDeliveryChannel.Email);
        if (row.HasSms) channels.Add(NoticeDeliveryChannel.Sms);
        return channels;
    }

    private sealed record TenantRecipientPreviewRow(
        int LeaseManagementPartyId,
        int TenantId,
        string DisplayName,
        NoticeRecipientRole Role,
        bool Eligible,
        bool HasPortal,
        bool HasPush,
        bool HasEmail,
        bool HasSms,
        string? Email,
        string? Phone,
        string Reason);

}
