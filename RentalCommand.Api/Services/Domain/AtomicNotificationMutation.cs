using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Notifications;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicNotificationMutationDomain
{
    MyAlerts,
    TeamRouting,
    TenantNoticePolicy,
    SeedTemplates,
    TemplateVersion,
    RestoreTemplate,
    MorningBriefingSettings,
    LateFeeSettings,
    DeviceRegister,
    DeviceUnregister,
    LandlordConversationRead,
    TenantConversationRead,
    MarkRead,
    MarkAllRead,
    Broadcast,
}

public sealed record AtomicNotificationMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicNotificationMutationDomain Domain,
    int EntityId,
    string ResourceKey,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey,
    [property: AtomicFingerprintIgnore]
    DateTime BusinessNowUtc = default) : IAtomicCommandData;

public sealed record AtomicNotificationMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    int AffectedCount,
    string? ResponseJson = null);

public sealed record AtomicDeviceMutationRequest(string Token, string? Platform) : IAtomicCommandData;

public sealed record AtomicConversationReadRequest(int TenantId) : IAtomicCommandData;

public sealed class AtomicNotificationMutationHandler
    : IAtomicCommandHandler<AtomicNotificationMutationCommand, AtomicNotificationMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicNotificationMutationHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicNotificationMutationResult> HandleAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync("WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        if (command.Domain is AtomicNotificationMutationDomain.LandlordConversationRead
            or AtomicNotificationMutationDomain.TenantConversationRead)
            await attempt.AcquireLockAsync("Conversation", command.EntityId, ct);
        var databaseNow = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        var mutationNow = BusinessNow(command, databaseNow);
        attempt.UseDatabaseWallClockForAudit(mutationNow);
        await AuthorizeAsync(command, _db, databaseNow, ct);

        return command.Domain switch
        {
            AtomicNotificationMutationDomain.MyAlerts =>
                await UpdateMyAlertsAsync(command, attempt, mutationNow, ct),
            AtomicNotificationMutationDomain.TeamRouting =>
                await ReplaceTeamRoutingAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.TenantNoticePolicy =>
                await UpsertPolicyAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.SeedTemplates =>
                await SeedTemplatesAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.TemplateVersion =>
                await CreateTemplateVersionAsync(command, attempt, databaseNow, restore: false, ct),
            AtomicNotificationMutationDomain.RestoreTemplate =>
                await CreateTemplateVersionAsync(command, attempt, databaseNow, restore: true, ct),
            AtomicNotificationMutationDomain.MorningBriefingSettings =>
                await UpdateMorningBriefingSettingsAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.LateFeeSettings =>
                await UpdateLateFeeAutomationSettingsAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.DeviceRegister =>
                await RegisterDeviceAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.DeviceUnregister =>
                await UnregisterDeviceAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.LandlordConversationRead =>
                await MarkConversationReadAsync(command, attempt, databaseNow, tenantViewer: false, ct),
            AtomicNotificationMutationDomain.TenantConversationRead =>
                await MarkConversationReadAsync(command, attempt, databaseNow, tenantViewer: true, ct),
            AtomicNotificationMutationDomain.MarkRead =>
                await MarkReadAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.MarkAllRead =>
                await MarkAllReadAsync(command, attempt, databaseNow, ct),
            AtomicNotificationMutationDomain.Broadcast =>
                await BroadcastAsync(command, attempt, databaseNow, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(command, _db, await context.ReadDatabaseClockUtcAsync(ct), ct);
    }

    private async Task<AtomicNotificationMutationResult> UpdateMyAlertsAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpdateMyAlertsRequest>(command);
        var db = _db;
        var row = await db.Set<UserAlertPreference>().SingleOrDefaultAsync(preference =>
            preference.PortfolioId == command.PortfolioId && preference.UserId == command.ActorUserId, ct);
        var operation = row is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (row is null)
        {
            row = new UserAlertPreference
            {
                PortfolioId = command.PortfolioId,
                UserId = command.ActorUserId,
                CreatedAtUtc = now,
            };
            db.Add(row);
        }
        row.EnableInApp = request.EnableInApp;
        row.EnableMobilePush = request.EnableMobilePush;
        row.EnableEmail = request.EnableEmail;
        row.EnableSms = request.EnableSms;
        row.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(row, Audit(command, nameof(UserAlertPreference), operation,
            "Personal notification destinations updated", operation == AuditLogOperation.Created ? 0 : row.Id));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(UserAlertPreference), row.Id, now);
        var response = await MyAlertsQuery(db, command.PortfolioId, command.ActorUserId).SingleAsync(ct);
        return Applied(row.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> ReplaceTeamRoutingAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpsertTeamRoutingRuleRequest>(command);
        var userIds = request.Recipients.Select(recipient => recipient.UserId).Distinct().ToArray();
        if (userIds.Length != request.Recipients.Count)
            throw new InvalidOperationException("A named recipient may appear only once in a routing rule.");
        if (request.Recipients.Any(recipient => string.IsNullOrWhiteSpace(recipient.Reason)))
            throw new InvalidOperationException("Every named recipient needs a clear routing reason.");
        if (request.PropertyId is not null
            && request.Topic is (TeamRoutingTopic.AccountAndSecurity or TeamRoutingTopic.MorningBriefing))
            throw new InvalidOperationException($"{request.Topic} routing is workspace-wide and cannot target one property.");

        var db = _db;
        if (request.PropertyId is int propertyId && !await db.Set<Property>().AsNoTracking()
                .AnyAsync(property => property.Id == propertyId && property.PortfolioId == command.PortfolioId
                    && property.DeletedAt == null, ct))
            throw new InvalidOperationException("The routing property is outside this workspace.");

        var validUserCount = await EligibleRoutingRecipients(command, db, request, now, userIds)
            .Distinct().CountAsync(ct);
        if (validUserCount != userIds.Length)
            throw new InvalidOperationException(
                "Every named recipient must hold the topic capability and property scope on one active assignment.");

        var rule = await db.Set<TeamRoutingRule>()
            .Include(candidate => candidate.Recipients)
            .SingleOrDefaultAsync(candidate => candidate.PortfolioId == command.PortfolioId
                && candidate.Topic == request.Topic && candidate.PropertyId == request.PropertyId, ct);
        var operation = rule is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (rule is null)
        {
            rule = new TeamRoutingRule
            {
                PortfolioId = command.PortfolioId,
                Topic = request.Topic,
                PropertyId = request.PropertyId,
                CreatedAtUtc = now,
            };
            db.Add(rule);
        }
        else
        {
            foreach (var recipient in rule.Recipients.ToArray())
            {
                db.Remove(recipient);
                attempt.BindSemanticAudit(recipient, Audit(command, nameof(TeamRoutingRuleRecipient),
                    AuditLogOperation.Deleted, "Team routing recipient replaced", recipient.Id));
            }
        }
        rule.UseWorkspaceAdministratorFallback = request.UseWorkspaceAdministratorFallback;
        rule.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(rule, Audit(command, nameof(TeamRoutingRule), operation,
            "Team notification routing replaced", operation == AuditLogOperation.Created ? 0 : rule.Id));
        await attempt.FlushBusinessAsync(ct);

        foreach (var requested in request.Recipients)
        {
            var recipient = new TeamRoutingRuleRecipient
            {
                TeamRoutingRuleId = rule.Id,
                PortfolioId = command.PortfolioId,
                UserId = requested.UserId,
                Reason = requested.Reason.Trim(),
            };
            db.Add(recipient);
            attempt.BindSemanticAudit(recipient, Audit(command, nameof(TeamRoutingRuleRecipient),
                AuditLogOperation.Created, "Named Team routing recipient assigned", 0));
        }
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(TeamRoutingRule), rule.Id, now);
        var response = await TeamRoutingResponseQuery(db, command.PortfolioId, rule.Id)
            .SingleAsync(ct);
        return Applied(rule.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> UpsertPolicyAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpsertTenantNoticePolicyRequest>(command);
        var reviewedJurisdiction = NormalizeJurisdiction(request.ReviewedJurisdictionCode);
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal
            && (!request.ConfirmJurisdictionReviewed || reviewedJurisdiction is null))
            throw new InvalidOperationException(
                "Legal notices require explicit jurisdiction and template review before Auto is allowed.");
        if (request.IncludeOccupant && (request.Classification == NoticeClassification.Legal
                || request.AutomationKey is "rent-reminder" or "late-rent-late-fee"))
            throw new InvalidOperationException(
                "Occupants are not eligible for financial or legal notices merely because they reside at the property.");
        if (request.IncludeEligibleGuarantor && request.Classification != NoticeClassification.Legal)
            throw new InvalidOperationException(
                "Guarantors receive only explicitly designated legally relevant notices.");

        var db = _db;
        var template = await TemplateResponseQuery(
                db, command.PortfolioId, request.WorkspaceNoticeTemplateVersionId)
            .SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Template version is not current for this workspace.");
        if (!string.Equals(template.SystemKey, request.AutomationKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected template must match the tenant notice automation.");
        if (request.Classification != template.Classification)
            throw new InvalidOperationException("The tenant notice classification must match its supplied template.");
        if (request.Mode == TenantNoticeMode.Auto && request.Classification == NoticeClassification.Legal
            && (template.JurisdictionReviewedAtUtc is null
                || NormalizeJurisdiction(template.JurisdictionCode) != reviewedJurisdiction))
            throw new InvalidOperationException(
                "The selected legal template must be reviewed for the same jurisdiction as this automation.");

        var row = await db.Set<TenantNoticePolicy>().SingleOrDefaultAsync(policy =>
            policy.PortfolioId == command.PortfolioId && policy.AutomationKey == request.AutomationKey, ct);
        var operation = row is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (row is null)
        {
            row = new TenantNoticePolicy
            {
                PortfolioId = command.PortfolioId,
                AutomationKey = request.AutomationKey.Trim(),
                CreatedAtUtc = now,
            };
            db.Add(row);
        }
        row.Mode = request.Mode;
        row.Classification = request.Classification;
        row.LeadDays = Math.Clamp(request.LeadDays, 0, 365);
        row.SendHourLocal = Math.Clamp(request.SendHourLocal, 0, 23);
        row.SendTenantPortal = request.SendTenantPortal;
        row.SendMobilePush = request.SendMobilePush;
        row.SendEmail = request.SendEmail;
        row.SendSms = request.SendSms;
        row.IncludePrimaryTenant = request.IncludePrimaryTenant;
        row.IncludeCoTenant = request.IncludeCoTenant;
        row.IncludeEligibleGuarantor = request.IncludeEligibleGuarantor;
        row.IncludeOccupant = request.IncludeOccupant;
        row.FailureBehavior = request.FailureBehavior;
        row.WorkspaceNoticeTemplateVersionId = template.Id;
        row.ReviewedJurisdictionCode = request.ConfirmJurisdictionReviewed ? reviewedJurisdiction : null;
        row.JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null;
        row.JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? command.ActorUserId : null;
        row.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(row, Audit(command, nameof(TenantNoticePolicy), operation,
            "Tenant notice automation policy updated", operation == AuditLogOperation.Created ? 0 : row.Id));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(TenantNoticePolicy), row.Id, now);
        var response = await TenantNoticePolicyResponseQuery(db, command.PortfolioId, row.Id)
            .SingleAsync(ct);
        return Applied(row.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> SeedTemplatesAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        var systems = await db.Set<SystemNoticeTemplateVersion>().AsNoTracking()
            .Where(system => system.Version == SuppliedNoticeTemplateBaseline.Version
                && !db.Set<WorkspaceNoticeTemplateVersion>().Any(workspace =>
                    workspace.PortfolioId == command.PortfolioId && workspace.SystemKey == system.SystemKey))
            .OrderBy(system => system.Id)
            .ToListAsync(ct);
        var workspaces = systems.Select(system => new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = command.PortfolioId,
            SystemKey = system.SystemKey,
            Version = system.Version,
            BasedOnSystemTemplateVersionId = system.Id,
            IsCustomized = false,
            Subject = system.Subject,
            Body = system.Body,
            JurisdictionCode = system.JurisdictionCode,
            CreatedByUserId = command.ActorUserId,
            CreatedAtUtc = now,
        }).ToArray();
        db.AddRange(workspaces);
        foreach (var workspace in workspaces)
            attempt.BindSemanticAudit(workspace, Audit(command, nameof(WorkspaceNoticeTemplateVersion),
                AuditLogOperation.Created, "Supplied tenant notice template provisioned", 0));
        if (workspaces.Length > 0) await attempt.FlushBusinessAsync(ct);

        var missingPolicies = await (
            from workspace in LatestTemplateQuery(db, command.PortfolioId).AsNoTracking()
            join system in db.Set<SystemNoticeTemplateVersion>().AsNoTracking()
                on workspace.BasedOnSystemTemplateVersionId equals system.Id
            where !db.Set<TenantNoticePolicy>().Any(policy =>
                policy.PortfolioId == command.PortfolioId
                && policy.AutomationKey == workspace.SystemKey)
            orderby workspace.Id
            select new
            {
                workspace.Id,
                workspace.SystemKey,
                system.Classification,
            }).ToListAsync(ct);
        var policies = missingPolicies.Select(template => new TenantNoticePolicy
        {
            PortfolioId = command.PortfolioId,
            AutomationKey = template.SystemKey,
            Mode = TenantNoticeMode.Draft,
            Classification = template.Classification,
            LeadDays = template.SystemKey is "lease-renewal-offer" or "month-to-month-offer" or "lease-non-renewal" ? 60 : 5,
            SendHourLocal = 9,
            SendTenantPortal = true,
            SendEmail = true,
            IncludePrimaryTenant = true,
            IncludeCoTenant = true,
            FailureBehavior = NoticeFailureBehavior.StopAndRequireReview,
            WorkspaceNoticeTemplateVersionId = template.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }).ToArray();
        db.AddRange(policies);
        foreach (var policy in policies)
            attempt.BindSemanticAudit(policy, Audit(command, nameof(TenantNoticePolicy),
                AuditLogOperation.Created, "Tenant notice policy provisioned from supplied template", 0));
        if (policies.Length > 0) await attempt.FlushBusinessAsync(ct);
        if (workspaces.Length == 0 && policies.Length == 0) return new(true, false, 0, 0);
        foreach (var workspace in workspaces)
        {
            StageDataUpdate(attempt, command, nameof(WorkspaceNoticeTemplateVersion),
                workspace.Id, now, $"workspace-template-{workspace.Id}");
        }
        foreach (var policy in policies)
        {
            StageDataUpdate(attempt, command, nameof(TenantNoticePolicy),
                policy.Id, now, $"tenant-notice-policy-{policy.Id}");
        }
        return new(true, true, 0, workspaces.Length + policies.Length);
    }

    private async Task<AtomicNotificationMutationResult> CreateTemplateVersionAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        bool restore,
        CancellationToken ct)
    {
        var db = _db;
        var current = await LatestTemplateQuery(db, command.PortfolioId)
            .SingleAsync(template => template.SystemKey == command.ResourceKey, ct);
        var system = restore
            ? await CurrentSystemTemplateQuery(db)
                .SingleAsync(template => template.SystemKey == command.ResourceKey, ct)
            : await db.Set<SystemNoticeTemplateVersion>()
                .SingleAsync(template => template.Id == current.BasedOnSystemTemplateVersionId, ct);
        var request = restore
            ? new CreateWorkspaceNoticeTemplateVersionRequest(
                system.Subject, system.Body, system.JurisdictionCode, false)
            : Read<CreateWorkspaceNoticeTemplateVersionRequest>(command);
        var policy = await db.Set<TenantNoticePolicy>().SingleOrDefaultAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId && candidate.AutomationKey == command.ResourceKey, ct)
            ?? throw new KeyNotFoundException("The tenant notice automation policy was not provisioned.");
        var reviewedJurisdiction = NormalizeJurisdiction(request.JurisdictionCode);
        if (system.Classification == NoticeClassification.Legal && policy.Mode == TenantNoticeMode.Auto
            && (!request.ConfirmJurisdictionReviewed || reviewedJurisdiction is null))
            throw new InvalidOperationException(
                "An automatically sent legal template must be explicitly reviewed for a jurisdiction before it is bound.");

        var next = new WorkspaceNoticeTemplateVersion
        {
            PortfolioId = command.PortfolioId,
            SystemKey = command.ResourceKey,
            Version = current.Version + 1,
            BasedOnSystemTemplateVersionId = system.Id,
            IsCustomized = !restore,
            Subject = request.Subject.Trim(),
            Body = request.Body.Trim(),
            JurisdictionCode = reviewedJurisdiction,
            JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null,
            JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? command.ActorUserId : null,
            CreatedByUserId = command.ActorUserId,
            CreatedAtUtc = now,
        };
        db.Add(next);
        attempt.BindSemanticAudit(next, Audit(command, nameof(WorkspaceNoticeTemplateVersion),
            AuditLogOperation.Created, restore ? "Supplied notice template restored" : "Notice template version created", 0));
        await attempt.FlushBusinessAsync(ct);

        policy.WorkspaceNoticeTemplateVersionId = next.Id;
        policy.Classification = system.Classification;
        policy.ReviewedJurisdictionCode = request.ConfirmJurisdictionReviewed ? reviewedJurisdiction : null;
        policy.JurisdictionReviewedAtUtc = request.ConfirmJurisdictionReviewed ? now : null;
        policy.JurisdictionReviewedByUserId = request.ConfirmJurisdictionReviewed ? command.ActorUserId : null;
        policy.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(policy, Audit(command, nameof(TenantNoticePolicy), AuditLogOperation.Updated,
            "Tenant notice policy saved with its template version", policy.Id));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(WorkspaceNoticeTemplateVersion), next.Id, now);
        var response = await TenantNoticePolicyResponseQuery(db, command.PortfolioId, policy.Id)
            .SingleAsync(ct);
        return Applied(next.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> MarkReadAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        var isStaff = await IsStaffAsync(command, db, now, ct);
        var notification = await db.Set<Notification>().SingleOrDefaultAsync(candidate =>
            candidate.Id == command.EntityId && candidate.PortfolioId == command.PortfolioId
            && (candidate.UserId == null || candidate.UserId == command.ActorUserId)
            && (isStaff || candidate.Type != "TenantMessage"), ct);
        if (notification is null) return Missing();
        if (await AtomicNotificationPersistence.MarkReadAsync(_db,
                attempt, command.PortfolioId, notification.Id, command.ActorUserId, isStaff, now, ct))
        {
            attempt.StageSemanticEvent(Audit(command, nameof(NotificationReadState), AuditLogOperation.Created,
                "Notification marked read", notification.Id), now);
            StageDataUpdate(attempt, command, nameof(Notification), notification.Id, now);
            return new(true, true, notification.Id, 1);
        }
        return new(true, false, notification.Id, 0);
    }

    private async Task<AtomicNotificationMutationResult> UpdateMorningBriefingSettingsAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpdateMorningBriefingSettingsRequest>(command);
        if (request.SendHourLocal is < 0 or > 23)
            throw new InvalidOperationException("Morning Briefing send hour must be between 0 and 23.");

        var settings = await _db.Set<AutomationSettings>()
            .SingleAsync(candidate => candidate.PortfolioId == command.PortfolioId, ct);
        settings.EnableMorningBriefing = request.Enabled;
        settings.MorningBriefingSendHourLocal = request.SendHourLocal;
        settings.MorningBriefingIncludeEmpty = request.IncludeEmpty;
        settings.UpdatedAtUtc = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(command, nameof(AutomationSettings), AuditLogOperation.Updated,
            "Morning Briefing schedule updated", settings.Id), now);
        StageDataUpdate(attempt, command, nameof(AutomationSettings), settings.Id, now);
        var response = await MorningBriefingSettingsQuery(_db, command.PortfolioId).SingleAsync(ct);
        return Applied(settings.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> UpdateLateFeeAutomationSettingsAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<UpdateLateFeeAutomationSettingsRequest>(command);
        if (request.LateFeeGraceDays is < 0 or > 31)
            throw new InvalidOperationException("Late fee grace days must be between 0 and 31.");

        var settings = await _db.Set<AutomationSettings>()
            .SingleAsync(candidate => candidate.PortfolioId == command.PortfolioId, ct);
        settings.EnableRentCharges = true;
        settings.EnableLateFees = request.EnableLateFees;
        settings.LateFeeGraceDays = request.LateFeeGraceDays;
        settings.UpdatedAtUtc = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(command, nameof(AutomationSettings), AuditLogOperation.Updated,
            "Late fee automation settings updated", settings.Id), now);
        StageDataUpdate(attempt, command, nameof(AutomationSettings), settings.Id, now);
        var response = await LateFeeAutomationSettingsQuery(_db, command.PortfolioId).SingleAsync(ct);
        return Applied(settings.Id, JsonSerializer.Serialize(response));
    }

    private async Task<AtomicNotificationMutationResult> RegisterDeviceAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<AtomicDeviceMutationRequest>(command);
        var token = request.Token.Trim();
        var platform = request.Platform?.Trim().ToLowerInvariant() ?? string.Empty;
        if (token.Length is 0 or > 500 || platform is not ("ios" or "android" or "web"))
            throw new InvalidOperationException("A valid device token and platform are required.");

        var row = await _db.Set<DeviceToken>().SingleOrDefaultAsync(candidate =>
            candidate.PortfolioId == command.PortfolioId && candidate.Token == token, ct);
        var operation = row is null ? AuditLogOperation.Created : AuditLogOperation.Updated;
        if (row is null)
        {
            row = new DeviceToken
            {
                PortfolioId = command.PortfolioId,
                Token = token,
                CreatedAt = now,
            };
            _db.Add(row);
        }
        row.UserId = command.ActorUserId;
        row.Platform = platform;
        row.LastSeenAt = now;
        attempt.BindSemanticAudit(row, Audit(command, nameof(DeviceToken), operation,
            "Push notification device registered", operation == AuditLogOperation.Created ? 0 : row.Id));
        await attempt.FlushBusinessAsync(ct);
        return Applied(row.Id);
    }

    private async Task<AtomicNotificationMutationResult> UnregisterDeviceAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<AtomicDeviceMutationRequest>(command);
        var token = request.Token.Trim();
        if (token.Length is 0 or > 500)
            throw new InvalidOperationException("A valid device token is required.");
        var row = await _db.Set<DeviceToken>().SingleOrDefaultAsync(candidate =>
            candidate.PortfolioId == command.PortfolioId && candidate.UserId == command.ActorUserId
            && candidate.Token == token, ct);
        if (row is null) return Missing();
        _db.Remove(row);
        attempt.BindSemanticAudit(row, Audit(command, nameof(DeviceToken), AuditLogOperation.Deleted,
            "Push notification device unregistered", row.Id));
        await attempt.FlushBusinessAsync(ct);
        return new(true, true, row.Id, 1);
    }

    private async Task<AtomicNotificationMutationResult> MarkConversationReadAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        bool tenantViewer,
        CancellationToken ct)
    {
        var request = Read<AtomicConversationReadRequest>(command);
        var conversations = tenantViewer
            ? TenantConversationQuery(command, _db, request.TenantId)
            : LandlordConversationQuery(command, _db, now);
        var conversation = await conversations.SingleOrDefaultAsync(candidate => candidate.Id == command.EntityId, ct);
        if (conversation is null) return Missing();

        var unread = tenantViewer ? conversation.TenantUnreadCount : conversation.LandlordUnreadCount;
        if (unread == 0) return new(true, false, conversation.Id, 0);
        var changed = await AtomicNotificationPersistence.MarkConversationReadAsync(_db,
            attempt, command.PortfolioId, conversation.Id, tenantViewer, now, ct);
        if (changed == 0) return new(true, false, conversation.Id, 0);
        attempt.StageSemanticEvent(Audit(command, nameof(Conversation), AuditLogOperation.Updated,
            tenantViewer ? "Tenant conversation marked read" : "Team conversation marked read", conversation.Id), now);
        StageDataUpdate(attempt, command, nameof(Conversation), conversation.Id, now);
        return new(true, true, conversation.Id, unread);
    }

    private async Task<AtomicNotificationMutationResult> MarkAllReadAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var isStaff = await IsStaffAsync(command, _db, now, ct);
        var result = await AtomicNotificationPersistence.MarkAllReadAsync(_db,
            attempt, command.PortfolioId, command.ActorUserId, isStaff, now, ct);
        if (result.Count > 0)
        {
            var notificationId = result.NotificationId
                ?? throw new InvalidOperationException(
                    "Mark-all-read inserted rows without returning a notification id.");
            attempt.StageSemanticEvent(Audit(command, nameof(NotificationReadState), AuditLogOperation.Created,
                $"{result.Count} notifications marked read", notificationId), now);
            StageDataUpdate(attempt, command, nameof(Notification), notificationId, now);
        }
        return new(true, result.Count > 0, 0, result.Count);
    }

    private async Task<AtomicNotificationMutationResult> BroadcastAsync(
        AtomicNotificationMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<CreateBroadcastNotificationRequest>(command);
        var notification = new Notification
        {
            PortfolioId = command.PortfolioId,
            Type = "System",
            Title = request.Title.Trim(),
            Message = request.Message.Trim(),
            Severity = NormalizeSeverity(request.Severity),
            CreatedAt = now,
        };
        _db.Add(notification);
        attempt.BindSemanticAudit(notification, Audit(command, nameof(Notification), AuditLogOperation.Created,
            "Workspace notification broadcast created", 0));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Notification), notification.Id, now);
        var response = await NotificationResponseQuery(_db, command.PortfolioId)
            .SingleAsync(candidate => candidate.Id == notification.Id, ct);
        return Applied(notification.Id, JsonSerializer.Serialize(response));
    }

    private async Task AuthorizeAsync(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var identityAuthorized = await IdentityAuthorized(command, db, now).AnyAsync(ct);
        if (!identityAuthorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
        var requiredCapability = command.Domain switch
        {
            AtomicNotificationMutationDomain.MyAlerts or AtomicNotificationMutationDomain.MarkRead
                or AtomicNotificationMutationDomain.MarkAllRead
                or AtomicNotificationMutationDomain.DeviceRegister
                or AtomicNotificationMutationDomain.DeviceUnregister
                or AtomicNotificationMutationDomain.LandlordConversationRead
                or AtomicNotificationMutationDomain.TenantConversationRead => null,
            AtomicNotificationMutationDomain.Broadcast => CapabilityKeys.TeamManage,
            _ => CapabilityKeys.NotificationsManage,
        };
        if (requiredCapability is not null && !await WorkspaceCapabilityAuthorized(
                command, db, now, requiredCapability).AnyAsync(ct))
            throw new UnauthorizedAccessException("The current Team role cannot manage this notification setting.");

        if (command.Domain == AtomicNotificationMutationDomain.LandlordConversationRead
            && !await LandlordConversationQuery(command, db, now)
                .AnyAsync(conversation => conversation.Id == command.EntityId, ct))
            throw new UnauthorizedAccessException("The current Team role cannot read this conversation.");
        if (command.Domain == AtomicNotificationMutationDomain.TenantConversationRead)
        {
            var request = Read<AtomicConversationReadRequest>(command);
            if (!await TenantConversationQuery(command, db, request.TenantId)
                    .AnyAsync(conversation => conversation.Id == command.EntityId, ct))
                throw new UnauthorizedAccessException("This conversation is outside the current tenant relationship.");
        }
    }

    private IQueryable<WorkspaceAccessContext> IdentityAuthorized(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        DateTime now) =>
        db.Set<WorkspaceAccessContext>().AsNoTracking().Where(context =>
            context.Id == command.AccessContextId && context.UserId == command.ActorUserId
            && context.PortfolioId == command.PortfolioId
            && context.AccessRevision == command.ExpectedAccessRevision
            && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now));

    private IQueryable<MembershipRoleAssignment> WorkspaceCapabilityAuthorized(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        string capability) =>
        db.Set<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
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
            && IdentityAuthorized(command, db, now).Any()
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == capability
                && grant.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Workspace));

    private IQueryable<int> EligibleRoutingRecipients(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        UpsertTeamRoutingRuleRequest request,
        DateTime now,
        int[] requestedUserIds)
    {
        var (capability, targetKind) = RoutingAuthorityFor(request.Topic);
        return from user in db.Set<ApplicationUser>().AsNoTracking()
            join context in db.Set<WorkspaceAccessContext>().AsNoTracking() on user.Id equals context.UserId
            join membership in db.Set<WorkspaceMembership>().AsNoTracking() on context.Id equals membership.AccessContextId
            join assignment in db.Set<MembershipRoleAssignment>().AsNoTracking()
                on new { WorkspaceMembershipId = membership.Id, membership.PortfolioId }
                equals new { assignment.WorkspaceMembershipId, assignment.PortfolioId }
            where requestedUserIds.Contains(user.Id)
                && context.PortfolioId == command.PortfolioId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= now
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.RoleProfile!.Capabilities.Any(grant =>
                    (request.Topic == TeamRoutingTopic.MorningBriefing
                        && grant.CapabilityDefinition!.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property
                        && (grant.CapabilityDefinition.Key == CapabilityKeys.RentalsRead
                            || grant.CapabilityDefinition.Key == CapabilityKeys.WorkRead
                            || grant.CapabilityDefinition.Key == CapabilityKeys.MoneyBalancesRead
                            || grant.CapabilityDefinition.Key == CapabilityKeys.LeasingShowingsManage
                            || grant.CapabilityDefinition.Key == CapabilityKeys.LeasingOnboardingManage))
                    || (request.Topic != TeamRoutingTopic.MorningBriefing
                        && grant.CapabilityDefinition!.Key == capability
                        && grant.CapabilityDefinition.AuthorizationTargetKind == targetKind))
                && (targetKind == CapabilityAuthorizationTargetKind.Workspace
                    || (request.PropertyId == null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PortfolioId == command.PortfolioId))))
                    || (request.PropertyId != null
                        && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                            || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                                && assignment.SelectedProperties.Any(scope =>
                                    scope.PortfolioId == command.PortfolioId
                                    && scope.PropertyId == request.PropertyId)))))
            select user.Id;
    }

    private Task<bool> IsStaffAsync(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct) =>
        db.Set<WorkspaceMembership>().AsNoTracking().AnyAsync(membership =>
            membership.PortfolioId == command.PortfolioId
            && membership.AccessContextId == command.AccessContextId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= now
            && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now), ct);

    private IQueryable<Conversation> LandlordConversationQuery(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        DateTime now)
    {
        var assignments = db.Set<MembershipRoleAssignment>().AsNoTracking().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= now
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > now)
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= now
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
            && assignment.RoleProfile!.Capabilities.Any(grant =>
                grant.CapabilityDefinition!.Key == CapabilityKeys.RentalsRead
                && grant.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property));

        return db.Set<Conversation>().Where(conversation =>
            conversation.PortfolioId == command.PortfolioId
            && ((conversation.PropertyId == null
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties))
                || (conversation.PropertyId != null
                    && assignments.Any(assignment =>
                        assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(selected =>
                                selected.PortfolioId == command.PortfolioId
                                && selected.PropertyId == conversation.PropertyId))))));
    }

    private IQueryable<Conversation> TenantConversationQuery(
        AtomicNotificationMutationCommand command,
        RentalCommandDbContext db,
        int tenantId) =>
        db.Set<Conversation>().Where(conversation =>
            conversation.Id == command.EntityId
            && conversation.PortfolioId == command.PortfolioId
            && conversation.TenantId == tenantId
            && db.Set<EffectiveTenantAccessProjection>().AsNoTracking().Any(access =>
                access.PortfolioId == command.PortfolioId
                && access.UserId == command.ActorUserId
                && access.AccessContextId == command.AccessContextId
                && access.AccessRevision == command.ExpectedAccessRevision
                && access.TenantId == tenantId));

    private IQueryable<MorningBriefingSettingsResponse> MorningBriefingSettingsQuery(
        RentalCommandDbContext db,
        int portfolioId) =>
        from settings in db.Set<AutomationSettings>().AsNoTracking()
        join portfolio in db.Set<Portfolio>().AsNoTracking()
            on settings.PortfolioId equals portfolio.Id
        where settings.PortfolioId == portfolioId && portfolio.DeletedAt == null
        select new MorningBriefingSettingsResponse(
            settings.EnableMorningBriefing,
            settings.MorningBriefingSendHourLocal,
            settings.MorningBriefingIncludeEmpty,
            portfolio.TimeZone == "" ? "America/New_York" : portfolio.TimeZone);

    private IQueryable<LateFeeAutomationSettingsResponse> LateFeeAutomationSettingsQuery(
        RentalCommandDbContext db,
        int portfolioId) =>
        from settings in db.Set<AutomationSettings>().AsNoTracking()
        join portfolio in db.Set<Portfolio>().AsNoTracking()
            on settings.PortfolioId equals portfolio.Id
        where settings.PortfolioId == portfolioId && portfolio.DeletedAt == null
        select new LateFeeAutomationSettingsResponse(
            settings.EnableRentCharges,
            settings.EnableLateFees,
            settings.LateFeeGraceDays);

    private IQueryable<MyAlertsResponse> MyAlertsQuery(
        RentalCommandDbContext db,
        int portfolioId,
        int userId) =>
        from context in db.Set<WorkspaceAccessContext>().AsNoTracking()
        join user in db.Set<ApplicationUser>().AsNoTracking() on context.UserId equals user.Id
        where context.PortfolioId == portfolioId && context.UserId == userId
            && context.Status == WorkspaceAccessContextStatus.Active
            && context.SuspendedAtUtc == null && context.RevokedAtUtc == null
        join saved in db.Set<UserAlertPreference>().AsNoTracking()
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

    private IQueryable<TeamRoutingRuleResponse> TeamRoutingResponseQuery(
        RentalCommandDbContext db,
        int portfolioId,
        int ruleId) =>
        from rule in db.Set<TeamRoutingRule>().AsNoTracking()
        where rule.PortfolioId == portfolioId && rule.Id == ruleId
        let recipientCount = rule.Recipients.Count()
        let recipientNames = string.Join(", ", rule.Recipients
            .OrderBy(recipient => recipient.User!.DisplayName)
            .ThenBy(recipient => recipient.UserId)
            .Select(recipient => recipient.User!.DisplayName))
        let explanations = string.Join(" ", rule.Recipients
            .OrderBy(recipient => recipient.User!.DisplayName)
            .ThenBy(recipient => recipient.UserId)
            .Select(recipient => recipient.User!.DisplayName + " receives this because " + recipient.Reason + "."))
        select new TeamRoutingRuleResponse(rule.Id, rule.Topic, rule.PropertyId,
            rule.PropertyId == null ? "All in-scope properties" : rule.Property!.Name,
            rule.UseWorkspaceAdministratorFallback, recipientCount,
            recipientCount == 0 ? "No named recipients" : recipientNames,
            recipientCount == 0 && rule.UseWorkspaceAdministratorFallback
                ? "No named recipient is assigned; active Workspace Administrators receive this topic."
                : explanations,
            rule.UpdatedAtUtc);

    private IQueryable<TenantNoticePolicyResponse> TenantNoticePolicyResponseQuery(
        RentalCommandDbContext db,
        int portfolioId,
        int policyId) =>
        from policy in db.Set<TenantNoticePolicy>().AsNoTracking()
        join template in db.Set<WorkspaceNoticeTemplateVersion>().AsNoTracking()
            on new { TemplateId = policy.WorkspaceNoticeTemplateVersionId, policy.PortfolioId }
            equals new { TemplateId = template.Id, template.PortfolioId }
        join system in db.Set<SystemNoticeTemplateVersion>().AsNoTracking()
            on template.BasedOnSystemTemplateVersionId equals system.Id
        let updateAvailable = db.Set<SystemNoticeTemplateVersion>().Any(candidate =>
            candidate.SystemKey == template.SystemKey && candidate.Version > system.Version)
        where policy.PortfolioId == portfolioId && policy.Id == policyId
        select new TenantNoticePolicyResponse(policy.Id, policy.AutomationKey, policy.Mode,
            policy.Classification, policy.LeadDays, policy.SendHourLocal, policy.SendTenantPortal,
            policy.SendMobilePush, policy.SendEmail, policy.SendSms, policy.IncludePrimaryTenant,
            policy.IncludeCoTenant, policy.IncludeEligibleGuarantor, policy.IncludeOccupant,
            policy.FailureBehavior, policy.WorkspaceNoticeTemplateVersionId, template.SystemKey,
            template.Version, template.Subject, template.Body, template.BasedOnSystemTemplateVersionId,
            system.Provenance, template.IsCustomized, updateAvailable, template.CreatedAtUtc,
            template.JurisdictionCode, template.JurisdictionReviewedAtUtc,
            policy.ReviewedJurisdictionCode, policy.JurisdictionReviewedAtUtc,
            policy.Classification != NoticeClassification.Legal
                || (policy.ReviewedJurisdictionCode != null && policy.JurisdictionReviewedAtUtc != null
                    && template.JurisdictionCode != null && template.JurisdictionReviewedAtUtc != null
                    && policy.ReviewedJurisdictionCode.Trim().ToUpper()
                        == template.JurisdictionCode.Trim().ToUpper()),
            policy.UpdatedAtUtc);

    private IQueryable<WorkspaceNoticeTemplateResponse> TemplateResponseQuery(
        RentalCommandDbContext db,
        int portfolioId,
        int templateVersionId) =>
        from workspace in LatestTemplateQuery(db, portfolioId)
        join system in db.Set<SystemNoticeTemplateVersion>().AsNoTracking()
            on workspace.BasedOnSystemTemplateVersionId equals system.Id
        where workspace.Id == templateVersionId
        let updateAvailable = db.Set<SystemNoticeTemplateVersion>().Any(candidate =>
            candidate.SystemKey == workspace.SystemKey && candidate.Version > system.Version)
        select new WorkspaceNoticeTemplateResponse(workspace.Id, workspace.SystemKey, workspace.Version,
            workspace.BasedOnSystemTemplateVersionId, workspace.IsCustomized, workspace.Subject,
            workspace.Body, system.Classification, workspace.JurisdictionCode,
            workspace.JurisdictionReviewedAtUtc, updateAvailable, workspace.CreatedAtUtc);

    private IQueryable<WorkspaceNoticeTemplateVersion> LatestTemplateQuery(
        RentalCommandDbContext db,
        int portfolioId) =>
        db.Set<WorkspaceNoticeTemplateVersion>().Where(row =>
            row.PortfolioId == portfolioId
            && !db.Set<WorkspaceNoticeTemplateVersion>().Any(newer =>
                newer.PortfolioId == row.PortfolioId && newer.SystemKey == row.SystemKey
                && newer.Version > row.Version));

    private IQueryable<SystemNoticeTemplateVersion> CurrentSystemTemplateQuery(
        RentalCommandDbContext db) =>
        db.Set<SystemNoticeTemplateVersion>().Where(row =>
            !db.Set<SystemNoticeTemplateVersion>().Any(newer =>
                newer.SystemKey == row.SystemKey && newer.Version > row.Version));

    private IQueryable<NotificationResponse> NotificationResponseQuery(
        RentalCommandDbContext db,
        int portfolioId) =>
        db.Set<Notification>().AsNoTracking().Where(notification =>
            notification.PortfolioId == portfolioId).Select(notification => new NotificationResponse
            {
                Id = notification.Id,
                Type = notification.Type,
                Title = notification.Title,
                Message = notification.Message,
                Severity = notification.Severity,
                RelatedEntityType = notification.RelatedEntityType,
                RelatedEntityId = notification.RelatedEntityId,
                IsRead = false,
                CreatedAt = notification.CreatedAt,
            });

    private (string CapabilityKey, CapabilityAuthorizationTargetKind TargetKind) RoutingAuthorityFor(
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
        TeamRoutingTopic.MorningBriefing =>
            (CapabilityKeys.RentalsRead, CapabilityAuthorizationTargetKind.Property),
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown Team routing topic."),
    };

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
        AtomicNotificationMutationCommand command,
        string entityType,
        int entityId,
        DateTime now,
        string? suffix = null) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix ?? "data-update"}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private AtomicSemanticAudit Audit(
        AtomicNotificationMutationCommand command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int entityId) =>
        new(command.PortfolioId, entityType, entityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private T Read<T>(AtomicNotificationMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("The notification update is invalid.");

    private DateTime BusinessNow(AtomicNotificationMutationCommand command, DateTime databaseNow)
    {
        if (command.BusinessNowUtc == default)
        {
            return databaseNow;
        }

        if (command.BusinessNowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Notification time must use UTC.");
        }

        return command.BusinessNowUtc;
    }

    private string? NormalizeJurisdiction(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private string NormalizeSeverity(string? severity) =>
        severity?.Trim().ToLowerInvariant() switch
        {
            "success" => "Success",
            "warning" => "Warning",
            "error" => "Error",
            "critical" => "Critical",
            _ => "Info",
        };

    private void Validate(AtomicNotificationMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128
            || ((command.Domain is AtomicNotificationMutationDomain.MarkRead
                    or AtomicNotificationMutationDomain.LandlordConversationRead
                    or AtomicNotificationMutationDomain.TenantConversationRead)
                && command.EntityId <= 0))
            throw new ArgumentException(
                "Workspace, user, access details, and delivery details are required.");
    }

    private AtomicNotificationMutationResult Missing() => new(false, false, 0, 0);
    private AtomicNotificationMutationResult Applied(int id, string? responseJson = null) =>
        new(true, true, id, 1, responseJson);
}

public static class AtomicNotificationMutation
{
    public static readonly AtomicJsonResultCodec<AtomicNotificationMutationResult> Codec =
        new("rental.notification-mutation.v1");

    public static AtomicNotificationMutationCommand Command<TRequest>(
        WorkspaceReadScope scope,
        AtomicNotificationMutationDomain domain,
        int entityId,
        string resourceKey,
        string operationKey,
        TRequest request) =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, entityId, resourceKey,
            JsonSerializer.Serialize(request), operationKey);

    public static AtomicNotificationMutationCommand Command<TRequest>(
        WorkspaceReadScope scope,
        AtomicNotificationMutationDomain domain,
        int entityId,
        string resourceKey,
        string operationKey,
        TRequest request,
        DateTime businessNowUtc) =>
        new(scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, entityId, resourceKey,
            JsonSerializer.Serialize(request), operationKey, businessNowUtc);

    public static AtomicCommandIdentity Identity(AtomicNotificationMutationCommand command) =>
        new($"rental.notification.{command.Domain.ToString().ToLowerInvariant()}",
            $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:" +
            $"{command.EntityId}:{command.ResourceKey}:{command.DeliveryIdempotencyKey}");
}
