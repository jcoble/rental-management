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
        await TemplateResponses(portfolioId).OrderBy(row => row.SystemKey).ToListAsync(ct);

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
        from row in
            (from rule in _db.TeamRoutingRules.AsNoTracking()
             join property in _db.Properties.AsNoTracking() on rule.PropertyId equals (int?)property.Id into properties
             from property in properties.DefaultIfEmpty()
             join recipient in _db.TeamRoutingRuleRecipients.AsNoTracking()
                 on new { RuleId = rule.Id, rule.PortfolioId }
                 equals new { RuleId = recipient.TeamRoutingRuleId, recipient.PortfolioId } into recipients
             from recipient in recipients.DefaultIfEmpty()
             join user in _db.Users.AsNoTracking() on recipient.UserId equals user.Id into users
             from user in users.DefaultIfEmpty()
             where rule.PortfolioId == portfolioId
             select new
             {
                 rule.Id,
                 rule.Topic,
                 rule.PropertyId,
                 PropertyName = property.Name,
                 rule.UseWorkspaceAdministratorFallback,
                 rule.UpdatedAtUtc,
                 RecipientId = (int?)recipient.Id,
                 RecipientUserId = (int?)recipient.UserId,
                 RecipientName = user.DisplayName,
                 RecipientReason = recipient.Reason,
             })
        group row by new
        {
            row.Id,
            row.Topic,
            row.PropertyId,
            row.PropertyName,
            row.UseWorkspaceAdministratorFallback,
            row.UpdatedAtUtc,
        }
        into rule
        orderby rule.Key.Topic, rule.Key.PropertyId
        let recipientCount = rule.Count(recipient => recipient.RecipientId != null)
        let recipientNames = string.Join(", ", rule
            .Where(recipient => recipient.RecipientId != null)
            .OrderBy(recipient => recipient.RecipientName)
            .ThenBy(recipient => recipient.RecipientUserId)
            .Select(recipient => recipient.RecipientName!))
        let recipientExplanations = string.Join(" ", rule
            .Where(recipient => recipient.RecipientId != null)
            .OrderBy(recipient => recipient.RecipientName)
            .ThenBy(recipient => recipient.RecipientUserId)
            .Select(recipient => recipient.RecipientName + " receives this because " + recipient.RecipientReason + "."))
        select new TeamRoutingRuleResponse(
            rule.Key.Id,
            rule.Key.Topic,
            rule.Key.PropertyId,
            rule.Key.PropertyId == null ? "All in-scope properties" : rule.Key.PropertyName!,
            rule.Key.UseWorkspaceAdministratorFallback,
            recipientCount,
            recipientCount == 0 ? "No named recipients" : recipientNames,
            recipientCount == 0 && rule.Key.UseWorkspaceAdministratorFallback
                ? "No named recipient is assigned; active Workspace Administrators receive this topic."
                : recipientExplanations,
            rule.Key.UpdatedAtUtc);

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

    private static TResponse ReadSnapshot<TResponse>(AtomicNotificationMutationResult result)
        where TResponse : class =>
        result.ResponseJson is not null
            ? JsonSerializer.Deserialize<TResponse>(result.ResponseJson)
                ?? throw new InvalidOperationException("Atomic notification result snapshot is invalid.")
            : throw new InvalidOperationException("Atomic notification result did not contain a response snapshot.");

}
